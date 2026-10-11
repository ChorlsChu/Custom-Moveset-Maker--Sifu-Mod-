using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Assets.Exports.Engine;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Assets.Objects.Properties;
using CUE4Parse.UE4.Objects.GameplayTags;
using CUE4Parse.UE4.Versions;
using CUE4Parse_Conversion.Animations;
using CUE4Parse_Conversion.Animations.PSA;
using CUE4Parse_Conversion.Meshes;
using CUE4Parse_Conversion.Meshes.PSK;
using Newtonsoft.Json;
using UAssetAPI;
using UAssetAPI.PropertyTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;

namespace SifuMovesetEditor;

public class MoveInfo
{
    public string DisplayName { get; set; } = "";
    public string FullPath { get; set; } = "";
    public string Character { get; set; } = "";
    public string WeaponType { get; set; } = "";
    public string Category { get; set; } = "";
    public bool IsUsed { get; set; } = false;
    public bool IsValid { get; set; } = true;
    public string EnemyType { get; set; } = "";

    public string SourceType => SourceTypeFromPath(FullPath, Category);

    public static string SourceTypeFromPath(string? fullPath, string? category = null)
    {
        if (string.Equals(category, "Custom", StringComparison.OrdinalIgnoreCase))
            return "Custom";

        if (!string.IsNullOrEmpty(fullPath) &&
            fullPath.StartsWith("Game/Animations/", StringComparison.OrdinalIgnoreCase))
            return "Animation File";

        if (IsAttackDbGamePath(fullPath))
            return "Attack DB";

        return "Other";
    }

    public static bool IsAttackDbGamePath(string? fullPath) =>
        !string.IsNullOrEmpty(fullPath) &&
        (fullPath.Contains("DB/AI/Archetypes", StringComparison.OrdinalIgnoreCase) ||
         fullPath.Contains("DB/_MainChar", StringComparison.OrdinalIgnoreCase));

    public string DisplayNameClean
    {
        get
        {
            var result = DisplayName;

            if (!string.IsNullOrEmpty(Character) && !string.IsNullOrEmpty(WeaponType))
            {
                var prefix = $"{Character}_attack_{WeaponType}_";
                if (result.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    result = result[prefix.Length..];
            }

            if (result == DisplayName && !string.IsNullOrEmpty(WeaponType))
            {
                var idx = result.IndexOf(WeaponType, StringComparison.OrdinalIgnoreCase);
                if (idx > 0)
                {
                    var afterWeapon = idx + WeaponType.Length;
                    if (afterWeapon < result.Length && result[afterWeapon] == '_')
                        afterWeapon++;
                    if (afterWeapon < result.Length)
                        result = result[afterWeapon..];
                }
            }

            if (result == DisplayName && !string.IsNullOrEmpty(Character))
            {
                var charAttackPrefix = $"{Character}_attack_";
                if (result.StartsWith(charAttackPrefix, StringComparison.OrdinalIgnoreCase))
                    result = result[charAttackPrefix.Length..];

                if (result == DisplayName)
                {
                    var charPrefix = $"{Character}_";
                    if (result.StartsWith(charPrefix, StringComparison.OrdinalIgnoreCase))
                        result = result[charPrefix.Length..];
                }
            }

            if (result == DisplayName)
            {
                var lastUnderscore = result.LastIndexOf('_');
                if (lastUnderscore > 0 && lastUnderscore < result.Length - 1)
                {
                    var candidate = result[(lastUnderscore + 1)..];
                    if (candidate.Length > 2)
                        result = candidate;
                }
            }

            if (string.Equals(Character, "MainChar", StringComparison.OrdinalIgnoreCase))
            {
                var mainCharPrefixes = new[] {
                    "Barehands_", "PunishRepulse_", "PunishKD_", "FollowUp_",
                    "LightCombo_", "Engage_", "Pressure_", "Punish_", "Skill_", "KD_"
                };
                foreach (var pfx in mainCharPrefixes)
                {
                    if (result.StartsWith(pfx, StringComparison.OrdinalIgnoreCase)
                        && result.Length > pfx.Length)
                    {
                        result = result[pfx.Length..];
                        break;
                    }
                }
            }

            return result;
        }
    }
}

public class GroupHeader
{
    public string Name { get; set; } = "";
    public int Level { get; set; }
    public int Count { get; set; }
    public string Subtitle { get; set; } = "";
    public bool IsExpanded { get; set; }
    public string ParentName { get; set; } = "";
}

public enum ComboNodeKind
{
    Vanilla = 0,
    ImportedNew = 1,
    UserCreated = 2
}

public class ComboNode
{
    public int Id { get; set; }
    public int TreeIndex { get; set; } = -1;
    public string Name { get; set; } = "";
    public string AnimPath { get; set; } = "";
    public string DefaultAnimPath { get; set; } = "";
    public string DefaultDBPath { get; set; } = "";
    public string SourceDBPath { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string ImportedDisplayName { get; set; } = "";
    public bool IsRoot { get; set; }
    public int Depth { get; set; }
    public string InputLabel { get; set; } = "";
    public string DirectionLabel { get; set; } = "";
    public string VanillaAnimPath { get; set; } = "";
    public bool IsRedirect { get; set; }
    public int RedirectTargetTreeIndex { get; set; } = -1;
    public int ResolvedRedirectNodeId { get; set; } = -1;
    // True when this vanilla redirect had >=1 of its own transition rows re-sourced onto its
    // target attack node at parse time (A' pass-through). Export routes new links from that
    // target back to this redirect's struct - the row must land where the engine reads it.
    public bool OwnedContinuation { get; set; }
    // AttackDB tuning (m_iWantedBuildupFrames / m_fGameplayRange). Null = leave vanilla as-is.
    public int? AttackBuildupFrames { get; set; }
    public float? AttackGameplayRange { get; set; }
    // MC_Attacks_Alt window on the original attack card, in frames of the replacement's
    // timeline. Null = copy the original window unchanged on export.
    public int? DelayWindowLo { get; set; }
    public int? DelayWindowHi { get; set; }
    // Provenance: Vanilla = parsed from the vanilla tree, ImportedNew = extra node that only
    // exists in the modded tree, UserCreated = authored in the editor (TreeIndex assigned on export).
    public ComboNodeKind Kind { get; set; } = ComboNodeKind.Vanilla;
    [Newtonsoft.Json.JsonIgnore]
    public bool IsImportedFromMod { get; set; }
    public bool IsActionNode { get; set; }
    public string ActionClass { get; set; } = "";
    public bool IsEventNode { get; set; }
    public string EventName { get; set; } = "";
    // Cross-asset import provenance: the combo file (Content-relative, no extension) whose
    // node struct at ImportSourceTreeIndex the exporter clones when appending this node.
    // Empty = no import (attack/redirect nodes use the target-tree template flow).
    public string ImportComboRel { get; set; } = "";
    public int ImportSourceTreeIndex { get; set; } = -1;
    public bool Removed { get; set; }
    // MainChar in-place replacement: export copies the source combo's m_AIAttackAction
    // (class import + instance export) or m_Event onto this node's own m_Nodes struct and
    // clears its attack slots; m_Transitions / m_NodeRedirect / m_Name stay untouched.
    public bool ReplacedByAiNode { get; set; }
    public string ReplacedAiKind { get; set; } = "";
    public string ReplacedAiClass { get; set; } = "";
    public string ReplacedImportComboRel { get; set; } = "";
    public int ReplacedSourceTreeIndex { get; set; } = -1;
    // Attack payload replacement (kind Attack): the attack card's game path whose
    // registry entry export writes into this node's m_AttackInfos slots, after
    // clearing m_AIAttackAction / m_Event. Empty for Action/Event replacements.
    public string ReplacedAttackCardRel { get; set; } = "";
    }

public class ComboEdge
{
    public int FromNodeId { get; set; }
    public int ToNodeId { get; set; }
    public string InputName { get; set; } = "";
    public bool IsRedirect { get; set; }
    // True when this link was created in the editor (not present in any parsed tree) -
    // export appends only these (plus links touching UserCreated nodes) to the combo tree.
    public bool IsNewLink { get; set; }
    // For A' pass-through edges: the redirect node whose m_Transitions row this edge came
    // from (raw id before reindex, remapped to the graph id during the final edge pass).
    // -1 = the edge belongs to the source node's own transitions.
    public int OwnerRedirectNodeId { get; set; } = -1;
    // Branch chances: explicit m_fProbability for the row this new link appends.
    // null = auto (default weight = average vanilla row, rebalanced on export so the
    // struct keeps summing to 100 and the engine's roll reaches the appended row).
    public float? NewLinkProbability { get; set; }
    // True when the Conditions & Chances dialog created this link while syncing a
    // cleared condition's entry back to the node's forward links (the dropped outcome
    // becomes its own chance row = mixup). Restore / re-picking the condition drops
    // these edges again; user-created links never carry the flag.
    public bool FromClearMixup { get; set; }
    // Merge into an existing conditioned row: the link gets NO appended chance row -
    // its target joins the row's m_TargetNodes under MergedIntoKey as a new outcome
    // key (TreeRowConditionEdit.AddedKeys holds the write, keyed the same way).
    // -1 = not merged. Serialized with the graph (ComboEdge saves directly through
    // SavedUnitCache.Edges); old saves deserialize -1.
    public int MergedIntoStruct { get; set; } = -1;
    public int MergedIntoRow { get; set; } = -1;
    public int MergedIntoKey { get; set; } = -1;
}

/// <summary>
/// One vanilla m_Transitions row, snapshotted at parse time for branch-chance editing.
/// Keyed in ComboGraph.TreeRows by the struct's tree index; RowIndex matches the row's
/// position in the asset array so export can patch m_fProbability in place. User edits
/// live in ComboGraph.TreeRowProbabilityOverrides (original stays here as the baseline).
/// </summary>
public class TreeRowInfo
{
    public int RowIndex { get; set; }
    public string InputName { get; set; } = "";
    public float OriginalProbability { get; set; } = 100f;
    public int KeyCount { get; set; } = 1;
    // m_TargetNodes values in map iteration order (-1 = non-int value); Keys mirrors
    // the key bytes so multi-key conditional rows keep their routing visible.
    public List<int> TargetTreeIndices { get; set; } = new();
    public List<byte> Keys { get; set; } = new();
    public bool ConditionPresent { get; set; }
    public string ConditionClass { get; set; } = "";
    // Flattened "prop = value" lines from the condition export (params only; the
    // internal m_PerConditionValueToNodeMap is excluded - engine semantics TBD).
    public List<string> ConditionParams { get; set; } = new();
    // m_ConditionInstance FPackageIndex value (0 = none, >0 = same-file export) -
    // lets condition-param edits key on the instance export at write time.
    public int ConditionInstanceIndex { get; set; }
    // Structured params behind ConditionParams strings; null/empty on old saves
    // (strings still display, editors need a fresh parse).
    public List<ConditionParamInfo>? ConditionParamList { get; set; }
}

/// <summary>
/// Node-level condition data from the m_Nodes struct: required gameplay tags
/// (m_RequiredTags) and conditional attack sets (m_ConditionalAttacks - gameplay
/// tag -> alternate m_Attacks), snapshotted at parse time and persisted per unit.
/// </summary>
public class TreeNodeConditionInfo
{
    public List<string> RequiredTags { get; set; } = new();
    public List<ConditionalAttackEntry> ConditionalAttacks { get; set; } = new();
}

/// <summary>One m_ConditionalAttacks pair: the gating tag plus the unique attack assets.</summary>
public class ConditionalAttackEntry
{
    public string TagName { get; set; } = "";
    public List<string> AttackNames { get; set; } = new();
}

/// <summary>
/// One condition parameter captured at parse time: dotted property path inside the
/// condition export (ObjectProperty hops + struct prefixes), the value string, and
/// the tag kind (Int/Float/Bool/Enum/Name/Str/Byte/Text, else ReadOnly) that picks
/// the editor type. Editable edits live in ComboGraph.TreeConditionParamOverrides.
/// </summary>
public class ConditionParamInfo
{
    public string Path { get; set; } = "";
    public string Kind { get; set; } = "ReadOnly";
    public string Value { get; set; } = "";
}

/// <summary>
/// One row-level condition edit: empty m_ConditionInstance to a null reference
/// (ClearCondition - the vanilla "instance ref empty" layout, with the surviving
/// target becoming the row's unconditional 255 entry), re-route
/// m_TargetNodes outcome keys (Retarget holds only changed keys -> new tree index),
/// and/or attach a condition instance the vanilla row did not carry: SetInstance points
/// at a same-file condition export (share - params are shared by definition), while
/// CloneSourceRel/CloneSourceExport describe a condition export in another combo file
/// to clone into this asset at export time. Cross-file clone param edits are keyed by
/// CloneVirtualKey - a stable negative "export index" that the exporter resolves to the
/// clone's new export index after AssetCloner appends it.
/// </summary>
public class TreeRowConditionEdit
{
    public bool ClearCondition { get; set; }
    public Dictionary<byte, int> Retarget { get; set; } = new();
    public int SetInstance { get; set; }
    public string? CloneSourceRel { get; set; }
    public int CloneSourceExport { get; set; } = -1;
    public string CloneClass { get; set; } = "";
    /// <summary>The Retarget[255] entry was seeded by a Clear (the collapse target
    /// shown as the row's unconditional route) rather than chosen by the user -
    /// Restore drops it so clear-then-restore never leaves a retarget nobody picked.</summary>
    public bool SeededCollapse { get; set; }
    public bool HasPendingInstance =>
        SetInstance > 0 || (!string.IsNullOrEmpty(CloneSourceRel) && CloneSourceExport >= 0);

    /// <summary>Merged links: new outcome key -> the link's target NODE id (graph id,
    /// resolved at export through resolveNodeIndex - unlike Retarget, which holds tree
    /// indices directly). Add-only: export inserts entries the row's m_TargetNodes
    /// lacks, so a merged link's target rides the row's own condition instead of
    /// getting a standalone chance row. Empty on old saves.</summary>
    public Dictionary<byte, int> AddedKeys { get; set; } = new();

    /// <summary>Outcome key -> graph NODE id for user-authored targets (TreeIndex -1
    /// nodes that only get their index at export). Complementary to Retarget (vanilla
    /// tree indices); the UI keeps the two mutually exclusive per key and export
    /// resolves these through resolveNodeIndex, same as AddedKeys. Empty on old saves.</summary>
    public Dictionary<byte, int> RetargetNodeIds { get; set; } = new();

    /// <summary>Deep copy - every clone site (undo snapshot, dialog row overlays)
    /// must stay in sync when a field is added.</summary>
    public TreeRowConditionEdit Clone() => new()
    {
        ClearCondition = ClearCondition,
        Retarget = new Dictionary<byte, int>(Retarget),
        SetInstance = SetInstance,
        CloneSourceRel = CloneSourceRel,
        CloneSourceExport = CloneSourceExport,
        CloneClass = CloneClass,
        SeededCollapse = SeededCollapse,
        AddedKeys = new Dictionary<byte, int>(AddedKeys),
        RetargetNodeIds = new Dictionary<byte, int>(RetargetNodeIds)
    };

    /// <summary>Retarget value sentinels for edge-keyed condition edits (rows that only
    /// exist after export): TargetBlocked omits the outcome key from m_TargetNodes so
    /// the branch never routes on it; TargetNatural routes to the edge's own target,
    /// resolved at export (the picker cannot list user-added nodes).</summary>
    public const int TargetBlocked = -1;
    public const int TargetNatural = -2;

    /// <summary>
    /// Stable negative key for a pending cross-file clone: derived from
    /// (source asset, source export index) so every dialog session and the exporter
    /// compute the same bucket for the same clone - rows sharing one clone share
    /// their param edits, and never collide with real (positive) export indices.
    /// </summary>
    public static int CloneVirtualKey(string? sourceRel, int sourceExport)
    {
        unchecked
        {
            int h = 17;
            foreach (char c in (sourceRel ?? "").ToLowerInvariant()) h = h * 31 + c;
            h = (h * 31 + sourceExport) & 0x7FFFFFFF;
            return h == 0 ? -1 : -h;
        }
    }
}

/// <summary>
/// Shared branch-chance math (dialog display + export writeback). A row is either
/// explicit (user-typed - always kept exactly) or automatic (weight = vanilla
/// probability, or the average vanilla weight for new links). With normalize = true -
/// only for structs that received an append - automatic rows share whatever remains
/// after the explicit values, so the struct sums to 100 and the engine's cumulative
/// 0-100 roll still reaches the appended rows.
/// </summary>
public static class TreeChanceMath
{
    public const float SumTarget = 100f;

    public readonly struct Row
    {
        public readonly float? Explicit;
        public readonly float AutoWeight;
        public Row(float? explicitValue, float autoWeight)
        {
            Explicit = explicitValue;
            AutoWeight = autoWeight;
        }
    }

    public static List<float> Compute(IReadOnlyList<Row> rows, bool normalize)
    {
        var result = new List<float>(rows.Count);
        if (!normalize)
        {
            foreach (var r in rows) result.Add(r.Explicit ?? r.AutoWeight);
            return result;
        }

        float explicitSum = 0f;
        float autoWeightSum = 0f;
        int autoCount = 0;
        foreach (var r in rows)
        {
            if (r.Explicit.HasValue) explicitSum += r.Explicit.Value;
            else { autoWeightSum += Math.Max(0f, r.AutoWeight); autoCount++; }
        }

        float remainder = SumTarget - explicitSum;
        float scale = autoWeightSum > 0f ? remainder / autoWeightSum : 0f;
        if (scale < 0f) scale = 0f;
        float equal = autoCount > 0 ? Math.Max(0f, remainder) / autoCount : 0f;

        foreach (var r in rows)
        {
            if (r.Explicit.HasValue) result.Add(r.Explicit.Value);
            else if (autoWeightSum > 0f) result.Add(Math.Max(0f, r.AutoWeight * scale));
            else result.Add(equal);
        }
        return result;
    }

    /// <summary>Default weight for new-link rows: the average of the vanilla rows'
    /// assigned values (overrides count at their typed value). All-new structs (Phase A)
    /// fall back to the full 100 so the rows split evenly.</summary>
    public static float NewRowAutoWeight(IReadOnlyList<Row> vanillaRows)
    {
        if (vanillaRows.Count == 0) return SumTarget;
        float sum = 0f;
        foreach (var r in vanillaRows) sum += r.Explicit ?? r.AutoWeight;
        return sum > 0f ? sum / vanillaRows.Count : SumTarget;
    }

    /// <summary>The target set one row will route to after condition overrides apply -
    /// mirrors ExportDialog.ApplyConditionOverrides exactly: a cleared row collapses to
    /// its outcome-0 value (else the first entry) re-keyed 255, then Retarget[255]
    /// overwrites it; an uncleared row rewrites per-key targets in place (Retarget keys
    /// absent from the map are skipped, negatives excluded). Non-target sentinels
    /// (blocked/natural) and duplicate values are the caller's concern. Shared by the
    /// dialog's clear/mixup coverage math and the export append dedup so they can never
    /// disagree about which targets an entry still serves.</summary>
    public static List<int> PostOverrideTargets(
        IReadOnlyList<byte> keys, IReadOnlyList<int> targets, TreeRowConditionEdit? edit)
    {
        var result = new List<int>();
        if (edit == null)
        {
            foreach (var t in targets) if (t >= 0) result.Add(t);
            return result;
        }

        if (edit.ClearCondition)
        {
            int survivor = -1;
            int n = Math.Min(keys.Count, targets.Count);
            for (int i = 0; i < n; i++)
            {
                if (keys[i] == 0) { survivor = targets[i]; break; }
            }
            if (survivor < 0 && n > 0) survivor = targets[0];
            if (edit.Retarget.TryGetValue(255, out var rt255) && rt255 >= 0) survivor = rt255;
            if (survivor >= 0) result.Add(survivor);
            return result;
        }

        int count = Math.Min(keys.Count, targets.Count);
        for (int i = 0; i < count; i++)
        {
            int v = edit.Retarget.TryGetValue(keys[i], out var rt) && rt >= 0 ? rt : targets[i];
            if (v >= 0) result.Add(v);
        }
        return result;
    }

    /// <summary>Struct a new link from <paramref name="fromNode"/> appends to - mirrors
    /// export Phase B's A' owner-writeback (single owning redirect wins; ambiguous or
    /// ownerless falls back to the node's own struct; custom nodes have none).</summary>
    public static int ResolveWriteStruct(ComboGraph graph, ComboNode fromNode)
    {
        if (fromNode.TreeIndex < 0) return -1;
        var owners = graph.Nodes.Where(n => n.IsRedirect && n.OwnedContinuation
            && n.ResolvedRedirectNodeId == fromNode.Id && n.TreeIndex >= 0).ToList();
        return owners.Count == 1 ? owners[0].TreeIndex : fromNode.TreeIndex;
    }
}

public class ComboGraph
{
    public string WeaponName { get; set; } = "";
    public int RawNodeCount { get; set; }
    public List<ComboNode> Nodes { get; set; } = [];
    public List<ComboEdge> Edges { get; set; } = [];
    public Dictionary<int, int> RedirectOriginalTargets { get; set; } = new();
    // m_ComboStartNodesContainer: AI start-state tickets -> first combo node.
    // Only present/edited for enemy archetype trees (MainChar trees have no AI tickets).
    public List<StartEntry> StartEntries { get; set; } = [];
    // Branch chances: vanilla m_Transitions rows per struct tree index, snapshotted at
    // parse time (empty on old saves - the dialog then falls back to new-link rows and
    // export still works, since it reads probabilities from the asset itself).
    public Dictionary<int, List<TreeRowInfo>> TreeRows { get; set; } = new();
    // Content-relative combo path the graph was parsed from; lets old saves (whose
    // TreeRows snapshot is empty) re-parse the vanilla rows on demand.
    public string SourceGamePath { get; set; } = "";
    // Session flag: a TreeRows merge from SourceGamePath was already attempted, so a
    // struct still missing from TreeRows is genuinely row-less (not an old save).
    public bool TreeRowsBackfillAttempted { get; set; }
    // User-edited branch chances: struct tree index -> (row index -> m_fProbability).
    // Presence marks the row modified; Reset All / node reset clear these.
    public Dictionary<int, Dictionary<int, float>> TreeRowProbabilityOverrides { get; set; } = new();
    // Node-level condition data (required tags + conditional attacks) per tree index,
    // snapshotted at parse time; persisted per unit like TreeRows.
    public Dictionary<int, TreeNodeConditionInfo> TreeNodeConditions { get; set; } = new();
    // User-edited condition PARAMS: condition export index (m_ConditionInstance value)
    // -> (dotted property path -> new value string). Keyed by the instance so rows
    // sharing one export stay consistent; presence marks a change.
    public Dictionary<int, Dictionary<string, string>> TreeConditionParamOverrides { get; set; } = new();
    // User-edited row conditions: struct tree index -> (row index -> clear/re-route).
    // Presence marks the row modified; Reset All / node reset clear these.
    public Dictionary<int, Dictionary<int, TreeRowConditionEdit>> TreeRowConditionOverrides { get; set; } = new();
    // Conditions on rows that do not exist in vanilla (every new link appends one row,
    // including the outgoing rows of appended nodes): edge key -> attach/re-route.
    // Retarget sentinels per TreeRowConditionEdit.TargetBlocked/TargetNatural; edits
    // whose edge no longer exists are orphans and are pruned on commit / ignored on export.
    public Dictionary<string, TreeRowConditionEdit> TreeEdgeConditionOverrides { get; set; } = new();
    // Vanilla conduit nodes (m_NodeRedirect passthroughs) stripped from the graph at
    // parse time: raw file index -> raw downstream file indices it routed to. Display
    // annotation only - rows keep their raw conduit target and export keeps it too
    // (the conduit struct survives in the asset). Empty on old saves.
    public Dictionary<int, List<int>> ConduitTargets { get; set; } = new();

    /// <summary>Stable key for condition edits on export-appended transition rows -
    /// one row per new (from, to, input) link, save/load/parse independent.</summary>
    public static string EdgeConditionKey(ComboEdge e)
        => $"{e.FromNodeId}>{e.ToNodeId}|{e.InputName}";

    /// <summary>
    /// Re-resolves every transition's TargetNodeId/TargetName against this graph's
    /// TreeIndex values. Used when start entries are recovered from a fresh vanilla
    /// parse for an old save: fresh-parse node ids may not match the saved graph, but
    /// TreeIndex (asset order) is stable. A miss (condensed conduit target, or a node
    /// the save no longer has) falls back to -1 while keeping TargetTreeIndex. Original*
    /// mirrors the result so recovered rows are an unmodified baseline.
    /// </summary>
    public void RemapStartEntryTargets(List<StartEntry> entries)
    {
        var byTreeIndex = Nodes.Where(n => n.TreeIndex >= 0).ToLookup(n => n.TreeIndex);
        foreach (var entry in entries)
        {
            foreach (var t in entry.Transitions)
            {
                var node = t.TargetTreeIndex >= 0
                    ? byTreeIndex[t.TargetTreeIndex].FirstOrDefault()
                    : null;
                t.TargetNodeId = node?.Id ?? -1;
                t.TargetName = node?.Name ?? "";
                t.OriginalTargetNodeId = t.TargetNodeId;
                t.OriginalTargetTreeIndex = t.TargetTreeIndex;
                t.OriginalTargetName = t.TargetName;
            }
        }
    }

    /// <summary>Follows a redirect node's ResolvedRedirectNodeId chain to the final
    /// non-redirect node (cycle-guarded, max 8 hops). Returns the input id when it
    /// exists in the graph and is not a redirect; -1 when the input id is absent,
    /// the chain is a loop, or it ends at an unresolved/missing node. Reflects user
    /// RedirectMods (live node state).</summary>
    public int ResolveRedirectFinal(int nodeId)
    {
        var visited = new HashSet<int>();
        int cur = nodeId;
        for (int hop = 0; hop < 8; hop++)
        {
            if (cur < 0 || !visited.Add(cur)) return -1;
            var n = Nodes.FirstOrDefault(x => x.Id == cur);
            if (n == null) return -1;
            if (!n.IsRedirect) return cur;
            if (n.ResolvedRedirectNodeId < 0) return -1;
            cur = n.ResolvedRedirectNodeId;
        }
        return -1;
    }

    /// <summary>
    /// Old saves (pre per-key editing) serialize multi-key start transitions with
    /// KeyCount + TargetSummary but empty Keys/KeyTarget* parallel lists, so the
    /// dialog can only render the view-only "0→7, 1→9" summary. Rebuild the lists
    /// from the summary ("key→treeIndex" pairs joined by ", ", U+2192 arrow) and
    /// resolve each key's node id/name from this graph's TreeIndex-stable nodes
    /// (no match = condensed/conduit target, keep the tree index - same rule the
    /// vanilla parse uses). OriginalKeyTargetTreeIndices mirrors the rebuilt values
    /// so untouched rows stay unmodified for the diff/write gate. Entries whose
    /// lists already line up with KeyCount (fresh parses, post-edit saves) are left
    /// alone; an empty or unparsable summary keeps the view-only fallback.
    /// </summary>
    public void RehydrateStartEntryKeyModels()
    {
        if (StartEntries == null || StartEntries.Count == 0) return;
        var byTreeIndex = Nodes.Where(n => n.TreeIndex >= 0).ToLookup(n => n.TreeIndex);
        foreach (var entry in StartEntries)
        {
            foreach (var t in entry.Transitions)
            {
                if (t.KeyCount <= 1 || t.Keys.Count == t.KeyCount) continue;
                if (string.IsNullOrWhiteSpace(t.TargetSummary)) continue;

                var keys = new List<byte>();
                var treeIndices = new List<int>();
                foreach (var part in t.TargetSummary.Split(',', StringSplitOptions.TrimEntries))
                {
                    int arrow = part.IndexOf('→');
                    if (arrow <= 0) { keys.Clear(); treeIndices.Clear(); break; }
                    if (!byte.TryParse(part[..arrow].Trim(), out var k)
                        || !int.TryParse(part[(arrow + 1)..].Trim(), out var v))
                    { keys.Clear(); treeIndices.Clear(); break; }
                    keys.Add(k);
                    treeIndices.Add(v);
                }
                if (keys.Count == 0) continue;

                t.KeyCount = Math.Max(1, keys.Count);
                t.Keys = keys;
                t.KeyTargetTreeIndices = treeIndices;
                t.KeyTargetNodeIds = new List<int>(treeIndices.Count);
                t.KeyTargetNames = new List<string>(treeIndices.Count);
                foreach (var v in treeIndices)
                {
                    var node = v >= 0 ? byTreeIndex[v].FirstOrDefault() : null;
                    t.KeyTargetNodeIds.Add(node?.Id ?? -1);
                    t.KeyTargetNames.Add(node?.Name ?? "");
                }
                t.OriginalKeyTargetTreeIndices = new List<int>(treeIndices);
            }
        }
    }
}

/// <summary>
/// One ComboStartNode element of m_ComboStartNodesContainer: an AI ticket
/// (BP_AIAttackTIcketNames) plus its possible first combo nodes with probabilities.
/// </summary>
public class StartEntry
{
    public string Label { get; set; } = "";
    public string TicketEnumValue { get; set; } = "";
    public string ConditionClass { get; set; } = "";
    public string PhaseName { get; set; } = "";
    public uint MCDominationFlags { get; set; }
    // Position in the vanilla container; export patches entries by this index
    // (MVP never adds or removes entries, so container order is stable).
    public int VanillaIndex { get; set; } = -1;
    // Entry dropped in the editor: kept in the model so export can drop it from
    // the container (and the dialog can report it), but skipped everywhere else.
    public bool Removed { get; set; }
    public List<StartTransition> Transitions { get; set; } = [];

    public StartEntry Clone() => new StartEntry
    {
        Label = Label,
        TicketEnumValue = TicketEnumValue,
        ConditionClass = ConditionClass,
        PhaseName = PhaseName,
        MCDominationFlags = MCDominationFlags,
        VanillaIndex = VanillaIndex,
        Removed = Removed,
        Transitions = Transitions.Select(t => t.Clone()).ToList()
    };

    public bool IsModified() => Removed || Transitions.Any(t => t.IsModified());
}

/// <summary>
/// One ComboTransition inside a start entry. Original* fields mirror the parsed
/// vanilla row and are never edited - they drive the change list and diff.
/// </summary>
public class StartTransition
{
    public float Probability { get; set; } = 100;
    // Current target: graph node id (-1 = target node was condensed away, keep TargetTreeIndex).
    public int TargetNodeId { get; set; } = -1;
    // Current target's tree index when known (-1 = user-created node, assigned on export).
    public int TargetTreeIndex { get; set; } = -1;
    public string TargetName { get; set; } = "";
    // m_TargetNodes map: key bytes (255 = unconditional; 0/1 = condition outcome routing).
    public byte KeyByte { get; set; } = 255;
    public int KeyCount { get; set; } = 1;
    // Multi-key conditional maps: parallel per-key lists (one entry per map key, in map
    // order) so every key's target is editable. Populated at parse for single-key rows
    // too; empty on old saves (dialog falls back to the view-only summary there).
    public List<byte> Keys { get; set; } = [];
    public List<int> KeyTargetTreeIndices { get; set; } = [];
    // Graph node id per key (-1 = condensed/unknown node, keep the tree index).
    public List<int> KeyTargetNodeIds { get; set; } = [];
    public List<string> KeyTargetNames { get; set; } = [];
    // "0→18, 1→19"-style summary (kept fresh by the dialog as keys are re-routed).
    public string TargetSummary { get; set; } = "";

    // Vanilla baseline (parse-time snapshot).
    public int OriginalIndex { get; set; } = -1;
    public float OriginalProbability { get; set; } = 100;
    public int OriginalTargetNodeId { get; set; } = -1;
    public int OriginalTargetTreeIndex { get; set; } = -1;
    public string OriginalTargetName { get; set; } = "";
    public List<int> OriginalKeyTargetTreeIndices { get; set; } = [];
    // Row deleted in the editor (kept in the model so export knows what to drop).
    public bool Removed { get; set; }

    public bool IsModified()
        => Removed
        || OriginalIndex < 0
        || Math.Abs(Probability - OriginalProbability) > 0.001f
        || TargetNodeId != OriginalTargetNodeId
        || (KeyTargetTreeIndices.Count > 0
            && !KeyTargetTreeIndices.SequenceEqual(OriginalKeyTargetTreeIndices));

    public StartTransition Clone()
    {
        var c = (StartTransition)MemberwiseClone();
        // MemberwiseClone would share the per-key lists with the graph's own rows.
        c.Keys = new List<byte>(Keys);
        c.KeyTargetTreeIndices = new List<int>(KeyTargetTreeIndices);
        c.KeyTargetNodeIds = new List<int>(KeyTargetNodeIds);
        c.KeyTargetNames = new List<string>(KeyTargetNames);
        c.OriginalKeyTargetTreeIndices = new List<int>(OriginalKeyTargetTreeIndices);
        return c;
    }
}

public class ModdedComboNodeInfo
{
    public int TreeIndex { get; set; }
    public string Name { get; set; } = "";
    public int RedirectTargetTreeIndex { get; set; } = -1;
    public List<string> AttackDbPaths { get; set; } = [];
    public List<ModdedTransitionInfo> Transitions { get; set; } = [];
    // Per-row snapshot (row index = position in the asset m_Transitions array) so the
    // import diff can align against vanilla TreeRows with a signature guard. Transitions
    // above stays the flat per-key list SurfaceModdedOnlyNodes builds edges from.
    public List<ModdedTransitionRowInfo> TransitionRows { get; set; } = [];
    // AI payload on the node struct: "Action" (m_AIAttackAction class), "Event"
    // (m_Event object/class name), or "" (no payload). AiClass holds the raw name.
    public string AiKind { get; set; } = "";
    public string AiClass { get; set; } = "";
    // Content-relative path (no extension) of the modded combo file this node came
    // from - the ImportComboRel surfaced AI nodes need so export can clone their
    // payload back out of the harvested CustomAssets copy of the mod.
    public string ModdedContentRel { get; set; } = "";
}

public class ModdedTransitionInfo
{
    public string InputName { get; set; } = "";
    public int TargetTreeIndex { get; set; } = -1;
}

/// <summary>One modded m_Transitions row with everything the chance/condition diff
/// needs: probability, condition class + instance export (1-based FPackageIndex value,
/// -1 = import reference or none), params, and the key->target map for the
/// signature guard.</summary>
public class ModdedTransitionRowInfo
{
    public int RowIndex { get; set; }
    public string InputName { get; set; } = "";
    public float Probability { get; set; } = 100f;
    public string ConditionClass { get; set; } = "";
    public int ConditionInstanceExport { get; set; } = -1;
    public List<ConditionParamInfo> ConditionParams { get; set; } = new();
    public List<byte> Keys { get; set; } = new();
    public List<int> TargetTreeIndices { get; set; } = new();
}

/// <summary>One m_ComboStartNodesContainer entry read from the modded file.</summary>
public class ModdedStartEntryInfo
{
    public string TicketEnumValue { get; set; } = "";
    public string Label { get; set; } = "";
    public string PhaseName { get; set; } = "";
    public uint MCDominationFlags { get; set; }
    public string ConditionClass { get; set; } = "";
    public List<ModdedStartTransitionInfo> Transitions { get; set; } = [];
}

/// <summary>One start-entry transition row from the modded file.</summary>
public class ModdedStartTransitionInfo
{
    public float Probability { get; set; } = 100f;
    public string ConditionClass { get; set; } = "";
    public List<byte> Keys { get; set; } = [];
    public List<int> KeyTargetTreeIndices { get; set; } = [];
}

/// <summary>Counts from ApplyModdedComboToVanilla (anim/redirect pass plus the AI
/// payload, chance/condition, and start-entry diffs).</summary>
public class ModdedApplyResult
{
    public int MoveChanges { get; set; }
    public int RetargetChanges { get; set; }
    public int Unmatched { get; set; }
    public int Surfaced { get; set; }
    public int AiChanges { get; set; }
    public int AiRemovalsSkipped { get; set; }
    public int ChanceOverrides { get; set; }
    public int CondClears { get; set; }
    public int CondInstances { get; set; }
    public int CondParamWrites { get; set; }
    public int CondSkipped { get; set; }
    public int StartEntriesChanged { get; set; }
    public int StartTransitionsChanged { get; set; }
    public int StartStructuralSkipped { get; set; }
}

public class BoneData
{
    public string name { get; set; } = "";
    public int parent { get; set; } = -1;
}

public class BindPoseTransform
{
    public PosVec position { get; set; } = new();
    public RotVec rotation { get; set; } = new();
}

public class PosVec
{
    public float x { get; set; }
    public float y { get; set; }
    public float z { get; set; }
}

public class RotVec
{
    public float x { get; set; }
    public float y { get; set; }
    public float z { get; set; }
    public float w { get; set; } = 1f;
}

public class AnimTrackData
{
    public string boneName { get; set; } = "";
    public int boneIndex { get; set; }
    public float[] times { get; set; } = [];
    public PosVec[] positions { get; set; } = [];
    public RotVec[] rotations { get; set; } = [];
    public PosVec[] scales { get; set; } = [];
}

public class AnimationJsonData
{
    public SkeletonJson skeleton { get; set; } = new();
    public AnimationJson animation { get; set; } = new();
}

public class SkeletonJson
{
    public BoneData[] bones { get; set; } = [];
    public BindPoseTransform[] bindPose { get; set; } = [];
}

public class AnimationJson
{
    public string name { get; set; } = "";
    public float duration { get; set; }
    public int numFrames { get; set; }
    public float fps { get; set; }
    public AnimTrackData[] tracks { get; set; } = [];
}

public class MeshJsonData
{
    public float[] positions { get; set; } = [];
    public float[] normals { get; set; } = [];
    public float[] uvs { get; set; } = [];
    public int[] indices { get; set; } = [];
    public int[] skinIndices { get; set; } = [];
    public float[] skinWeights { get; set; } = [];
    public string[] boneNames { get; set; } = [];
    public float[] bindPose { get; set; } = [];
    public int[] boneParents { get; set; } = [];
    public List<MeshMaterialGroup>? materials { get; set; }
}

public class AnimationParser : IDisposable
{
    private DefaultFileProvider? _provider;
    private DefaultFileProvider? _overlayProvider;
    private string _gameRootPath = "";
    private string _contentPath = "";
    private string ContentDir => Setup.ContentDetector.ResolveContentDir(_contentPath);
    public bool IsLoaded => _provider != null;

    private MeshTextureResolver? _texResolver;
    private const int MeshCacheMax = 15;
    private readonly object _meshCacheSync = new();
    private readonly Dictionary<string, (LinkedListNode<string> node, MeshJsonData data)> _meshCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _meshCacheOrder = new();
    // FindMeshPath walks Content/Characters recursively; memoized so unmapped characters
    // don't repeat the directory scan on every mesh load (null results memoized too).
    private readonly object _findMeshPathSync = new();
    private readonly Dictionary<string, string?> _findMeshPathMemo = new(StringComparer.OrdinalIgnoreCase);
    public ConcurrentDictionary<string, string> AnimToDbPath { get; } = new();
    public Dictionary<string, (float hitFrame, int buildupFrame)> AnimToTiming { get; } = new();

    private static readonly HashSet<string> SkipDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "01_FacialPoseAsset", "Weapon", "Activities",
        "Barks", "Dash", "Death", "Deflect", "Detection",
        "DialogGestures", "DizzyState", "Fidget", "GiveUp",
        "Guard", "HitReactions", "HitsAnims", "IncapacitatedState",
        "KnockDownState", "Locomotion", "Parry", "PushedState",
        "StructureBrokenState", "Traversal", "Turns", "WeaponActions",
        "WeaponAction", "Conditions", "GenericData", "VitalPointsDBs",
        "Variations", "_Arena", "_OLD", "_Advanced", "_Base",
        "_MainGame", "_Miniboss", "_Tests", "_old"
    };

    private static readonly Dictionary<string, string> CharacterMeshPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Grunt"] = "Game/Characters/PNJ/Grunt/M/Meshes/Hideout3/SK_Grunt_M_Hideout_03_Fighter_01",
        ["FireDisciple"] = "Game/Characters/PNJ/Disicple/M/Meshes/SK_Disciple_M_Hideout02_Fighter_01",
        ["FlashKick"] = "Game/Characters/PNJ/FlashKick/Meshes/Hideout03/SK_FlashKick_Hideout_03_FlashBlade_01",
        ["BigGuy"] = "Game/Characters/PNJ/BigGuy/Meshes/Hideout03/SK_BigGuy_Hideout03_Fighter_01",
        ["BodyGuard"] = "Game/Characters/PNJ/BodyGuards/M/Meshes/Hideout03/SK_BodyGuard_M_Hideout03_Fighter_01",
        ["Servant"] = "Game/Characters/PNJ/Servant/M/Meshes/Hideout03/SK_Servant_M_Hideout03_Fighter_01",
        ["Fajar"] = "Game/Characters/Boss/Fajar/Meshes/SK_Fajar_HD0",
        ["Sean"] = "Game/Characters/Boss/Sean/Meshes/SK_Sean_HD0",
        ["Kuroki"] = "Game/Characters/Boss/Kuroki/Meshes/SK_Kuroki_HD0",
        ["Yang"] = "Game/Characters/Boss/Yang/Meshes/SK_Yang_Hideout00",
        ["Fengjie"] = "Game/Characters/Boss/Fengjie/Meshes/SK_Fengjie_HD0",
        ["MainChar"] = "Game/Characters/MainChar/M/Meshes/SK_M_MainChar_01",
    };

    public void Initialize(string gameRootPath, string contentPath)
    {
        _gameRootPath = gameRootPath;
        _contentPath = contentPath;
        _provider = null;

        _provider = new DefaultFileProvider(
            gameRootPath,
            SearchOption.AllDirectories,
            new VersionContainer(EGame.GAME_UE4_26),
            StringComparer.OrdinalIgnoreCase
        );
        _provider.Initialize();

        LoadEngineContent(gameRootPath);

        var texCacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SifuMovesetEditor", "Thumbnails", "tex");
        _texResolver = new MeshTextureResolver(_provider, ContentDir, texCacheDir);
        ClearMeshCache();
    }

    private void ClearMeshCache()
    {
        lock (_meshCacheSync)
        {
            _meshCache.Clear();
            _meshCacheOrder.Clear();
        }
    }

    private void LoadEngineContent(string gameRootPath)
    {
        try
        {
            var candidates = new[]
            {
                Path.Combine(gameRootPath, "Engine", "Content"),
                Path.Combine(Directory.GetParent(gameRootPath)?.FullName ?? "", "Engine", "Content"),
            };

            string? engineContentDir = null;
            foreach (var candidate in candidates)
            {
                if (!string.IsNullOrEmpty(candidate) && Directory.Exists(candidate))
                {
                    engineContentDir = candidate;
                    break;
                }
            }

            if (engineContentDir == null)
            {
                LogDebug($"Engine content not found at: {candidates[0]} or parent path");
                return;
            }

            var engineRoot = Path.GetDirectoryName(engineContentDir)!;
            var baseDir = new DirectoryInfo(engineRoot);
            var filesAdded = 0;

            foreach (var file in Directory.GetFiles(engineContentDir, "*.uasset", SearchOption.AllDirectories))
            {
                var fileInfo = new FileInfo(file);
                var gameFile = new OsGameFile(baseDir, fileInfo, "Engine/", _provider!.Versions);
                _provider.Files.AddFiles(new Dictionary<string, GameFile> { [gameFile.Path] = gameFile });
                filesAdded++;
            }

            foreach (var file in Directory.GetFiles(engineContentDir, "*.uexp", SearchOption.AllDirectories))
            {
                var fileInfo = new FileInfo(file);
                var gameFile = new OsGameFile(baseDir, fileInfo, "Engine/", _provider!.Versions);
                _provider.Files.AddFiles(new Dictionary<string, GameFile> { [gameFile.Path] = gameFile });
                filesAdded++;
            }

            LogDebug($"Loaded {filesAdded} engine content files from {engineContentDir}");
        }
        catch (Exception ex)
        {
            LogDebug($"Failed to load engine content: {ex.Message}");
        }
    }

    public void SetOverlayProvider(DefaultFileProvider? provider)
    {
        try
        {
            if (_overlayProvider != null && !ReferenceEquals(_overlayProvider, provider))
                (_overlayProvider as IDisposable)?.Dispose();
        }
        catch { }
        _overlayProvider = provider;
    }

    public DefaultFileProvider? CreateOverlayProvider(string contentRoot)
    {
        if (_provider == null || !Directory.Exists(contentRoot)) return null;
        try
        {
            var provider = new DefaultFileProvider(
                contentRoot,
                SearchOption.AllDirectories,
                _provider.Versions,
                StringComparer.OrdinalIgnoreCase);

            // Do NOT call provider.Initialize(): it mounts keys as "Content/..." (directory name),
            // which FixPath can never produce for "Game/..." requests. Mount everything in one
            // batch under "Game/Content/" instead so ProjectName resolves to "Game" and
            // FixPath("Game/DB/X") -> "Game/Content/DB/X.uasset" matches our keys exactly.
            var baseDir = new DirectoryInfo(contentRoot);
            var overlayFiles = new Dictionary<string, GameFile>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in Directory.GetFiles(contentRoot, "*", SearchOption.AllDirectories))
            {
                var ext = Path.GetExtension(file).ToLowerInvariant();
                if (ext != ".uasset" && ext != ".uexp" && ext != ".ubulk") continue;
                var gameFile = new OsGameFile(baseDir, new FileInfo(file), "Game/Content/", provider.Versions);
                overlayFiles[gameFile.Path] = gameFile;
            }
            provider.Files.AddFiles(overlayFiles);

            LogDebug($"[OVERLAY] Mounted {overlayFiles.Count} files from {contentRoot} " +
                     $"(prefix Game/Content/, ProjectName={provider.ProjectName})");
            return provider;
        }
        catch (Exception ex)
        {
            LogDebug($"[OVERLAY] Create failed: {ex.Message}");
            return null;
        }
    }

    public int MountCustomIntoProvider(string customRoot)
    {
        if (_provider == null || !Directory.Exists(customRoot)) return 0;
        int filesAdded = 0;
        try
        {
            // Mount under "<ProjectName>/Content/" so FixPath("Game/<rel>") resolves to
            // "<ProjectName>/Content/<rel>.uasset" — plain "Game/" keys are unreachable
            // because FixPath rewrites the "Game" root to ProjectName + "/Content/".
            var prefix = (string.IsNullOrEmpty(_provider.ProjectName) ? "GameContent" : _provider.ProjectName) + "/Content/";
            var baseDir = new DirectoryInfo(customRoot);
            var customFiles = new Dictionary<string, GameFile>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in Directory.GetFiles(customRoot, "*", SearchOption.AllDirectories))
            {
                var ext = Path.GetExtension(file).ToLowerInvariant();
                if (ext != ".uasset" && ext != ".uexp" && ext != ".ubulk") continue;
                var gameFile = new OsGameFile(baseDir, new FileInfo(file), prefix, _provider.Versions);
                customFiles[gameFile.Path] = gameFile;
                filesAdded++;
            }
            if (customFiles.Count > 0)
                _provider.Files.AddFiles(customFiles, readOrder: 1);
            LogDebug($"[CUSTOM] Mounted {filesAdded} custom anim files from {customRoot} (prefix {prefix})");
        }
        catch (Exception ex)
        {
            LogDebug($"[CUSTOM] Mount failed: {ex.Message}");
        }
        return filesAdded;
    }

    private static void LogDebug(string message)
    {
        try { File.AppendAllText(Path.Combine(Directory.GetCurrentDirectory(), "error.log"), $"[{DateTime.Now:HH:mm:ss}] {message}\n"); } catch { }
    }

    public List<MoveInfo> ScanAnimations()
    {
        var moves = new List<MoveInfo>();
        var animsPath = Path.Combine(ContentDir, "Animations");
        if (!Directory.Exists(animsPath)) return moves;

        foreach (var characterDir in Directory.GetDirectories(animsPath))
        {
            var character = Path.GetFileName(characterDir);
            if (SkipDirectories.Contains(character)) continue;

            var attacksDir = Path.Combine(characterDir, "Attacks");
            if (!Directory.Exists(attacksDir))
                attacksDir = Path.Combine(characterDir, "Attack");
            if (!Directory.Exists(attacksDir)) continue;

            foreach (var weaponDir in Directory.GetDirectories(attacksDir))
            {
                var weaponType = NormalizeWeaponType(Path.GetFileName(weaponDir));
                if (SkipDirectories.Contains(weaponType)) continue;

                ScanAttackMoves(weaponDir, character, weaponType, "", moves);
            }

            foreach (var file in Directory.GetFiles(attacksDir, "*.uasset"))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                var relPath = file.Substring(_contentPath.Length).TrimStart('\\', '/').Replace('\\', '/');
                var gamePath = "Game/" + relPath.Replace(".uasset", "");
                moves.Add(new MoveInfo
                {
                    DisplayName = name,
                    FullPath = gamePath,
                    Character = character,
                    WeaponType = "New",
                    Category = ""
                });
            }
        }

        return moves.OrderBy(m => m.Character)
                     .ThenBy(m => m.WeaponType)
                     .ThenBy(m => m.Category)
                     .ThenBy(m => m.DisplayName)
                      .ToList();
    }

    public List<MoveInfo> ScanGetUpAnims()
    {
        var moves = new List<MoveInfo>();
        var animsPath = Path.Combine(ContentDir, "Animations");
        if (!Directory.Exists(animsPath)) return moves;

        var getUpFiles = new (string character, string relDir, string fileName)[]
        {
            ("FireDisciple", "KnockDownState/Barehands/GetUp", "FireDisciple_barehands_getUp_front"),
            ("FlashKick", "KnockDownState/GetUp", "Flashkick_barehands_KnockDown_getUp_front"),
        };

        foreach (var (character, relDir, fileName) in getUpFiles)
        {
            var filePath = Path.Combine(animsPath, character, relDir, fileName + ".uasset");
            if (!File.Exists(filePath)) continue;

            var gamePath = "Game/Animations/" + character + "/" + relDir + "/" + fileName;
            moves.Add(new MoveInfo
            {
                DisplayName = fileName,
                FullPath = gamePath,
                Character = character,
                WeaponType = "BareHands",
                Category = "GetUp",
                IsUsed = true
            });
        }

        return moves;
    }

    public List<MoveInfo> ScanStanceAnims()
    {
        var moves = new List<MoveInfo>();
        var animsPath = Path.Combine(ContentDir, "Animations");
        if (!Directory.Exists(animsPath)) return moves;

        foreach (var characterDir in Directory.GetDirectories(animsPath))
        {
            var character = Path.GetFileName(characterDir);
            if (SkipDirectories.Contains(character)) continue;

            var locoDir = Path.Combine(characterDir, "Locomotion");
            if (!Directory.Exists(locoDir)) continue;

            var found = FindStanceAnim(locoDir, character);
            if (found != null)
                moves.Add(found);
        }

        return moves.OrderBy(m => m.Character).ToList();
    }

    private MoveInfo? FindStanceAnim(string dir, string character)
    {
        var candidates = new List<(string file, string name, bool isV1, bool isNorth)>();
        CollectStanceCandidates(dir, candidates);

        if (candidates.Count == 0) return null;

        var best = candidates
            .OrderByDescending(c => c.isV1)
            .ThenByDescending(c => c.isNorth)
            .First();

        var relPath = best.file.Substring(_contentPath.Length).TrimStart('\\', '/').Replace('\\', '/');
        var gamePath = "Game/" + relPath.Replace(".uasset", "");
        var weapon = Path.GetFileName(Path.GetDirectoryName(dir)) ?? "";

        return new MoveInfo
        {
            DisplayName = $"{character}_stance",
            FullPath = gamePath,
            Character = character,
            WeaponType = weapon,
            Category = "Combat Stance"
        };
    }

    private void CollectStanceCandidates(string dir, List<(string file, string name, bool isV1, bool isNorth)> candidates)
    {
        foreach (var file in Directory.GetFiles(dir, "*.uasset"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            var lower = name.ToLowerInvariant();
            if (lower.StartsWith("blend")) continue;
            if (!lower.Contains("north_tense") && !lower.Contains("front_tense")) continue;

            var isV1 = file.Contains("\\V1\\") || file.Contains("/V1/");
            var isNorth = lower.Contains("north");
            candidates.Add((file, name, isV1, isNorth));
        }

        foreach (var subDir in Directory.GetDirectories(dir))
            CollectStanceCandidates(subDir, candidates);
    }

    public void BuildAnimToDbMapping()
    {
        AnimToDbPath.Clear();
        if (_provider == null) return;

        var archetypesRoot = Path.Combine(ContentDir, "DB", "AI", "Archetypes");
        if (Directory.Exists(archetypesRoot))
        {
            foreach (var charDir in Directory.GetDirectories(archetypesRoot))
            {
                if (SkipDirectories.Contains(Path.GetFileName(charDir))) continue;
                var attacksDir = Path.Combine(charDir, "Attacks");
                if (Directory.Exists(attacksDir))
                    ScanDbDirForMapping(attacksDir);
            }
        }

        var mainCharCombos = Path.Combine(ContentDir, "DB", "_MainChar", "Combos", "Attacks");
        if (Directory.Exists(mainCharCombos))
            ScanDbDirForMapping(mainCharCombos);

        LogDebug($"[MAP] Built anim→DB mapping: {AnimToDbPath.Count} entries");
    }

    private static bool IsNonAttackDbAsset(string fileName)
    {
        var lower = fileName.ToLowerInvariant();
        return lower.Contains("hitboxdata")
            || lower.Contains("datatable")
            || lower.Contains("playratecurve");
    }

    public List<MoveInfo> ScanArchetypeAttackDbs()
    {
        var moves = new List<MoveInfo>();
        if (_provider == null) return moves;

        var archetypesDir = Path.Combine(ContentDir, "DB", "AI", "Archetypes");
        if (Directory.Exists(archetypesDir))
        {
            foreach (var charDir in Directory.GetDirectories(archetypesDir))
            {
                var character = Path.GetFileName(charDir);
                if (SkipDirectories.Contains(character)) continue;
                character = CanonicalCharacterName(character);
                ScanAttackDbDir(charDir, character, moves);
            }
        }

        var mainCharAttacks = Path.Combine(ContentDir, "DB", "_MainChar", "Combos", "Attacks");
        if (Directory.Exists(mainCharAttacks))
            ScanAttackDbDir(mainCharAttacks, "MainChar", moves);

        return moves
            .OrderBy(m => m.Character, StringComparer.OrdinalIgnoreCase)
            .ThenBy(m => m.WeaponType, StringComparer.OrdinalIgnoreCase)
            .ThenBy(m => m.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Attack cards under the persistent CustomAssets root - the same scan shape as
    /// ScanArchetypeAttackDbs but rooted at customRoot. Character comes from the
    /// DB/AI/Archetypes/&lt;unit&gt; folder so the Custom section groups by unit, and
    /// Category is forced to "Custom" so the Custom tab picks them up. Requires
    /// MountCustomIntoProvider(CustomAssets.Root) first so HasAttackStruct resolves
    /// the game path against the mounted provider.
    /// </summary>
    public List<MoveInfo> ScanCustomAttackDbs(string customRoot)
    {
        var moves = new List<MoveInfo>();
        if (_provider == null || !Directory.Exists(customRoot)) return moves;

        try
        {
            var archetypesDir = Path.Combine(customRoot, "DB", "AI", "Archetypes");
            if (Directory.Exists(archetypesDir))
            {
                foreach (var charDir in Directory.GetDirectories(archetypesDir))
                {
                    var character = Path.GetFileName(charDir);
                    if (SkipDirectories.Contains(character)) continue;
                    character = CanonicalCharacterName(character);
                    ScanCustomAttackDbDir(customRoot, charDir, charDir, character, moves);
                }
            }

            var mainCharAttacks = Path.Combine(customRoot, "DB", "_MainChar", "Combos", "Attacks");
            if (Directory.Exists(mainCharAttacks))
                ScanCustomAttackDbDir(customRoot, mainCharAttacks, mainCharAttacks, "MainChar", moves);
        }
        catch (Exception ex)
        {
            LogDebug($"[CUSTOM] attack card scan failed: {ex.Message}");
        }

        return moves
            .OrderBy(m => m.Character, StringComparer.OrdinalIgnoreCase)
            .ThenBy(m => m.WeaponType, StringComparer.OrdinalIgnoreCase)
            .ThenBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void ScanCustomAttackDbDir(string customRoot, string baseDir, string dir, string character, List<MoveInfo> moves)
    {
        foreach (var file in Directory.GetFiles(dir, "*.uasset"))
        {
            var fileName = Path.GetFileNameWithoutExtension(file);
            if (IsNonAttackDbAsset(fileName)) continue;

            var relDir = Path.GetRelativePath(baseDir, Path.GetDirectoryName(file)!).Replace('\\', '/');
            var weaponType = EnemyAttackScanner.WeaponTypeFromRelDir(relDir);

            var relPath = Path.GetRelativePath(customRoot, file).Replace('\\', '/');
            var gamePath = "Game/" + relPath[..^".uasset".Length];

            if (!HasAttackStruct(gamePath))
            {
                LogDebug($"[CUSTOM] dropped (no m_Attack struct): {gamePath}");
                continue;
            }

            moves.Add(new MoveInfo
            {
                DisplayName = fileName,
                FullPath = gamePath,
                Character = character,
                WeaponType = weaponType,
                Category = "Custom",
                IsUsed = true,
                EnemyType = character
            });
        }

        foreach (var subDir in Directory.GetDirectories(dir))
        {
            var dirName = Path.GetFileName(subDir);
            var isOldDir = dirName.Equals("_old", StringComparison.OrdinalIgnoreCase);
            if (SkipDirectories.Contains(dirName) && !isOldDir) continue;
            ScanCustomAttackDbDir(customRoot, baseDir, subDir, character, moves);
        }
    }

    private void ScanAttackDbDir(string dir, string character, List<MoveInfo> moves)
    {
        foreach (var file in Directory.GetFiles(dir, "*.uasset"))
        {
            var fileName = Path.GetFileNameWithoutExtension(file);
            if (IsNonAttackDbAsset(fileName)) continue;

            string baseDir;
            if (string.Equals(character, "MainChar", StringComparison.OrdinalIgnoreCase))
                baseDir = Path.Combine(ContentDir, "DB", "_MainChar", "Combos", "Attacks");
            else
                baseDir = Path.Combine(ContentDir, "DB", "AI", "Archetypes", character);

            var relDir = Path.GetRelativePath(baseDir, Path.GetDirectoryName(file)!).Replace('\\', '/');

            var category = EnemyAttackScanner.DetermineCategory(relDir, fileName);
            var weaponType = EnemyAttackScanner.WeaponTypeFromRelDir(relDir);

            var relPath = file.Substring(_contentPath.Length).TrimStart('\\', '/').Replace('\\', '/');
            var gamePath = "Game/" + relPath.Replace(".uasset", "");

            var hasStruct = HasAttackStruct(gamePath);
            if (fileName.Contains("Grab", StringComparison.OrdinalIgnoreCase))
                LogDebug($"[GRAB_SCAN] {gamePath}: hasStruct={hasStruct} cat={category} weapon={weaponType}");
            if (!hasStruct)
            {
                LogDebug($"[LIBRARY] dropped (no m_Attack struct): {gamePath}");
                continue;
            }

            moves.Add(new MoveInfo
            {
                DisplayName = fileName,
                FullPath = gamePath,
                Character = character,
                WeaponType = weaponType,
                Category = category,
                IsUsed = true,
                EnemyType = character
            });
        }

        foreach (var subDir in Directory.GetDirectories(dir))
        {
            var dirName = Path.GetFileName(subDir);
            var isOldDir = dirName.Equals("_old", StringComparison.OrdinalIgnoreCase);
            if (SkipDirectories.Contains(dirName) && !isOldDir)
            {
                if (dirName.Contains("Grab", StringComparison.OrdinalIgnoreCase) ||
                    dirName.Contains("FarGrab", StringComparison.OrdinalIgnoreCase))
                    LogDebug($"[GRAB_SCAN] skipped dir: {subDir}");
                continue;
            }
            if (isOldDir)
                LogDebug($"[LIBRARY] scanning _old dir: {subDir}");
            ScanAttackDbDir(subDir, character, moves);
        }
    }

    private bool HasAttackStruct(string gamePath)
    {
        try
        {
            var dbObj = _provider?.SafeLoadPackageObject<UObject>(gamePath);
            if (dbObj == null && _overlayProvider != null)
                dbObj = _overlayProvider.SafeLoadPackageObject<UObject>(gamePath);
            if (dbObj == null)
            {
                if (gamePath.Contains("Grab", StringComparison.OrdinalIgnoreCase))
                    LogDebug($"[GRAB_SCAN] {gamePath}: dbObj=null");
                return false;
            }

            var mAttack = dbObj.Properties.FirstOrDefault(p => p.Name.Text == "m_Attack");
            var ok = mAttack?.Tag is StructProperty attackStruct &&
                     attackStruct.Value.StructType is FStructFallback;
            if (gamePath.Contains("Grab", StringComparison.OrdinalIgnoreCase))
                LogDebug($"[GRAB_SCAN] {gamePath}: m_Attack={(mAttack == null ? "missing" : mAttack.Tag?.GetType().Name ?? "null-tag")} structType={(mAttack?.Tag is StructProperty sp ? sp.Value.StructType?.GetType().Name ?? "null" : "n/a")} ok={ok}");
            return ok;
        }
        catch (Exception ex)
        {
            if (gamePath.Contains("Grab", StringComparison.OrdinalIgnoreCase))
                LogDebug($"[GRAB_SCAN] {gamePath}: exception {ex.Message}");
            return false;
        }
    }

    private void ScanDbDirForMapping(string dir)
    {
        foreach (var file in Directory.GetFiles(dir, "*.uasset"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            var relPath = file.Substring(_contentPath.Length).TrimStart('\\', '/').Replace('\\', '/');
            var gamePath = "Game/" + relPath.Replace(".uasset", "");

            try
            {
                var dbObj = _provider?.SafeLoadPackageObject<UObject>(gamePath);
                if (dbObj == null) continue;

                var mAttack = dbObj.Properties.FirstOrDefault(p => p.Name.Text == "m_Attack");
                if (mAttack?.Tag is StructProperty attackStruct &&
                    attackStruct.Value.StructType is FStructFallback attackData)
                {
                    var mAnimation = attackData.Properties.FirstOrDefault(p => p.Name.Text == "m_Animation");
                    if (mAnimation?.Tag is ObjectProperty animObj && animObj.Value != null)
                    {
                        var resolved = animObj.Value.ResolvedObject;
                        if (resolved != null)
                        {
                            var animPath = NormalizeAnimPath(resolved.GetPathName());
                            if (!string.IsNullOrEmpty(animPath))
                                AnimToDbPath.TryAdd(animPath, gamePath);
                        }
                    }
                }
            }
            catch { }
        }

        foreach (var subDir in Directory.GetDirectories(dir))
            ScanDbDirForMapping(subDir);
    }

    private static readonly string[] AttackDataTablePaths =
    [
        "Game/DB/AI/Archetypes/Grunt/Attacks/Grunt_AttacksDatatable",
        "Game/DB/AI/Archetypes/Grunt/Attacks/Grunt_WeaponsDatatable",
        "Game/DB/AI/Archetypes/FlashKick/Attacks/FlashKick_AttackData",
        "Game/DB/AI/Archetypes/BigGuy/Attacks/BigGuy_AttackData",
        "Game/DB/AI/Archetypes/Bodyguard/Attacks/BodyGuard_AttackData",
        "Game/DB/AI/Archetypes/FireDisciple/Attacks/FireDisciple_AttackData",
        "Game/DB/AI/Archetypes/FireDisciple/Attacks/FireDisciple_Staff_AttackData",
        "Game/DB/AI/Archetypes/Fajar/Attacks/Fajar_AttacksDatatable",
        "Game/DB/AI/Archetypes/Sean/Attacks/Sean_AttacksDatatable",
        "Game/DB/AI/Archetypes/Kuroki/Kuroki_AttacksDatatable",
        "Game/DB/AI/Archetypes/Fengjie/Attacks/Fengjie_AttacksDatatable",
        "Game/DB/AI/Archetypes/Yang/Attacks/Yang_Attacks",
        "Game/DB/Attacks/WUGUAN_Attacks",
    ];

    public HashSet<string> ScanUsedAnimations()
    {
        var usedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (_provider == null) return usedPaths;

        foreach (var dtPath in AttackDataTablePaths)
        {
            try
            {
                var table = _provider.SafeLoadPackageObject<UDataTable>(dtPath);
                if (table == null)
                {
                    LogDebug($"[DT] Could not load: {dtPath}");
                    continue;
                }

                LogDebug($"[DT] Loaded {dtPath}: {table.RowMap.Count} rows, struct={table.RowStructName}");

                if (table.RowMap.Count == 0) continue;

                var firstRow = table.RowMap.Values.First();
                var fieldNames = firstRow.Properties.Select(p => $"{p.Name.Text}[{p.PropertyType.Text}]").ToList();
                LogDebug($"[DT]   Fields: {string.Join(", ", fieldNames)}");

                foreach (var (rowName, rowData) in table.RowMap)
                {
                    ScanRowForAnimPaths(rowData, usedPaths);
                }
            }
            catch (Exception ex)
            {
                LogDebug($"[DT] Error reading {dtPath}: {ex.Message}");
            }
        }

        LogDebug($"[DT] Total used animation paths found: {usedPaths.Count}");
        foreach (var p in usedPaths.Take(10))
            LogDebug($"[DT]   Sample: {p}");
        return usedPaths;
    }

    private void ScanRowForAnimPaths(FStructFallback row, HashSet<string> usedPaths)
    {
        foreach (var prop in row.Properties)
        {
            try
            {
                if (prop.Tag == null) continue;

                if (prop.PropertyType.Text == "SoftObjectProperty" && prop.Tag is SoftObjectProperty softObj)
                {
                    var assetPath = softObj.Value.AssetPathName.Text;
                    if (!string.IsNullOrEmpty(assetPath))
                    {
                        var normalized = NormalizeAnimPath(assetPath);
                        if (normalized.Contains("Anim", StringComparison.OrdinalIgnoreCase))
                            usedPaths.Add(normalized);
                    }
                }
                else if (prop.PropertyType.Text == "StrProperty" && prop.Tag is StrProperty strVal)
                {
                    var val = strVal.Value;
                    if (!string.IsNullOrEmpty(val) && val.Contains("Anim", StringComparison.OrdinalIgnoreCase))
                    {
                        usedPaths.Add(NormalizeAnimPath(val));
                    }
                }
                else if (prop.PropertyType.Text == "NameProperty" && prop.Tag is NameProperty nameVal)
                {
                    var val = nameVal.Value.Text;
                    if (!string.IsNullOrEmpty(val) && val.Contains("Anim", StringComparison.OrdinalIgnoreCase))
                    {
                        usedPaths.Add(NormalizeAnimPath(val));
                    }
                }
                else if (prop.PropertyType.Text == "ObjectProperty" && prop.Tag is ObjectProperty objProp)
                {
                    var val = objProp.Value;
                    if (val != null)
                    {
                        var text = val.ToString();
                        if (!string.IsNullOrEmpty(text) && text.Contains("Anim", StringComparison.OrdinalIgnoreCase))
                        {
                            usedPaths.Add(NormalizeAnimPath(text));
                        }
                    }
                }
                else if (prop.PropertyType.Text == "StructProperty" && prop.Tag is StructProperty structProp)
                {
                    var structText = structProp.Value?.ToString();
                    if (!string.IsNullOrEmpty(structText) && structText.Contains("Anim", StringComparison.OrdinalIgnoreCase)
                        && structText.Contains('/'))
                    {
                        foreach (var segment in structText.Split(','))
                        {
                            var trimmed = segment.Trim().Trim('"');
                            if (trimmed.Contains("Anim", StringComparison.OrdinalIgnoreCase) && trimmed.Contains('/'))
                            {
                                var normalized = NormalizeAnimPath(trimmed);
                                if (!string.IsNullOrEmpty(normalized))
                                    usedPaths.Add(normalized);
                            }
                        }
                    }
                }
            }
            catch { }
        }
    }

    public void ScanDataTableTiming()
    {
        AnimToTiming.Clear();
        if (_provider == null) return;

        foreach (var dtPath in AttackDataTablePaths)
        {
            try
            {
                var table = _provider.SafeLoadPackageObject<UDataTable>(dtPath);
                if (table == null || table.RowMap.Count == 0) continue;

                foreach (var (rowName, rowData) in table.RowMap)
                {
                    string? animPath = null;
                    float hitFrame = 0;
                    int buildupFrame = 0;
                    bool hasHitFrame = false;
                    bool hasBuildup = false;

                    foreach (var prop in rowData.Properties)
                    {
                        if (prop.Tag == null) continue;

                        if (prop.PropertyType.Text == "SoftObjectProperty" && prop.Tag is SoftObjectProperty softObj)
                        {
                            var assetPath = softObj.Value.AssetPathName.Text;
                            if (!string.IsNullOrEmpty(assetPath))
                                animPath = NormalizeAnimPath(assetPath);
                        }
                        else if (prop.Name.Text == "m_fHitFrame" && prop.Tag is FloatProperty fp)
                        {
                            hitFrame = fp.Value;
                            hasHitFrame = true;
                        }
                        else if (prop.Name.Text == "m_iLastBuildupFrame" && prop.Tag is IntProperty ip)
                        {
                            buildupFrame = ip.Value;
                            hasBuildup = true;
                        }
                    }

                    if (!string.IsNullOrEmpty(animPath) && hasHitFrame && hasBuildup)
                        AnimToTiming[animPath] = (hitFrame, buildupFrame);
                }
            }
            catch (Exception ex)
            {
                LogDebug($"[DT] Error reading timing from {dtPath}: {ex.Message}");
            }
        }

        LogDebug($"[DT] Built anim→timing mapping: {AnimToTiming.Count} entries");
    }

    internal static string NormalizeAnimPath(string path)
    {
        path = path.Replace("\\", "/");
        if (path.StartsWith("/"))
            path = path.Substring(1);

        var contentIdx = path.IndexOf("/Content/", StringComparison.OrdinalIgnoreCase);
        if (contentIdx >= 0)
            path = "Game/" + path.Substring(contentIdx + "/Content/".Length);

        var lastSlash = path.LastIndexOf('/');
        var lastDot = path.LastIndexOf('.');
        if (lastDot > lastSlash)
            path = path[..lastDot];

        return path;
    }

    internal static string NormalizeWeaponType(string raw)
    {
        return raw.ToLowerInvariant() switch
        {
            "barehands" or "bare_hands" => "BareHands",
            "staff" => "Staff",
            "blade" or "blades" or "machete" or "dagger" or "daggers" => "Blades",
            "bat" or "bats" or "blunt" => "Bats",
            "meteor" or "hammer" or "meteorhammer" or "meteor_hammer" => "MeteorHammer",
            "tristaff" => "TriStaff",
            _ => raw
        };
    }

    private void ScanAttackMoves(string dir, string character, string weaponType, string category, List<MoveInfo> moves)
    {
        foreach (var file in Directory.GetFiles(dir, "*.uasset"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            var relPath = file.Substring(_contentPath.Length).TrimStart('\\', '/').Replace('\\', '/');
            var gamePath = "Game/" + relPath.Replace(".uasset", "");

            moves.Add(new MoveInfo
            {
                DisplayName = name,
                FullPath = gamePath,
                Character = character,
                WeaponType = weaponType,
                Category = category
            });
        }

        foreach (var subDir in Directory.GetDirectories(dir))
        {
            var subCat = Path.GetFileName(subDir);
            if (SkipDirectories.Contains(subCat)) continue;
            ScanAttackMoves(subDir, character, weaponType, subCat, moves);
        }
    }

    public List<string> GetAvailableCharacters()
    {
        return CharacterMeshPaths.Keys.OrderBy(k => k).ToList();
    }

    private static string CanonicalCharacterName(string character)
    {
        foreach (var key in CharacterMeshPaths.Keys)
        {
            if (string.Equals(key, character, StringComparison.OrdinalIgnoreCase))
                return key;
        }
        return character;
    }

    public ComboGraph? LoadMainCharComboTree()
    {
        if (_provider == null) return null;

        try
        {
            var obj = _provider.SafeLoadPackageObject<UObject>("Game/DB/_MainChar/Combos/MainChar_ComboTree");
            if (obj == null)
            {
                LogDebug("[COMBO] Could not load MainChar_ComboTree");
                return null;
            }

            var graph = ParseComboTreeFromObject(obj);
            if (graph != null) graph.SourceGamePath = "Game/DB/_MainChar/Combos/MainChar_ComboTree";
            return graph;
        }
        catch (Exception ex)
        {
            LogDebug($"[COMBO] Error loading MainChar combo tree: {ex.Message}");
            return null;
        }
    }

    public ComboGraph? LoadComboTreeFromPath(string gamePath, string weaponName)
    {
        if (_provider == null) return null;

        try
        {
            var obj = _provider.SafeLoadPackageObject<UObject>(gamePath);
            if (obj == null)
            {
                LogDebug($"[COMBO] Could not load combo tree at {gamePath}");
                return null;
            }

            var graph = ParseComboTreeFromObject(obj, weaponName);
            if (graph != null) graph.SourceGamePath = gamePath;
            return graph;
        }
        catch (Exception ex)
        {
            LogDebug($"[COMBO] Error loading combo tree {gamePath}: {ex.Message}");
            return null;
        }
    }

    public ComboGraph? LoadModdedComboTree(string moddedComboTreeDir)
    {
        if (_provider == null || string.IsNullOrEmpty(_contentPath))
        {
            LogDebug("[IMPORT] Vanilla parser not initialized");
            return null;
        }

        var moddedUasset = Path.Combine(moddedComboTreeDir, "MainChar_ComboTree.uasset");
        var moddedUexp = Path.Combine(moddedComboTreeDir, "MainChar_ComboTree.uexp");

        if (!File.Exists(moddedUasset))
        {
            LogDebug($"[IMPORT] Modded combo tree not found: {moddedUasset}");
            return null;
        }

        var vanillaDir = Path.Combine(ContentDir, "DB", "_MainChar", "Combos");
        var vanillaUasset = Path.Combine(vanillaDir, "MainChar_ComboTree.uasset");
        var vanillaUexp = Path.Combine(vanillaDir, "MainChar_ComboTree.uexp");

        if (!File.Exists(vanillaUasset))
        {
            LogDebug($"[IMPORT] Vanilla combo tree not found: {vanillaUasset}");
            return null;
        }

        var backupDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "VanillaBackup");
        Directory.CreateDirectory(backupDir);

        try
        {
            File.Copy(vanillaUasset, Path.Combine(backupDir, "MainChar_ComboTree.uasset"), true);
            if (File.Exists(vanillaUexp))
                File.Copy(vanillaUexp, Path.Combine(backupDir, "MainChar_ComboTree.uexp"), true);

            LogDebug($"[IMPORT] Backed up vanilla combo tree to {backupDir}");

            File.Copy(moddedUasset, vanillaUasset, true);
            if (File.Exists(moddedUexp))
                File.Copy(moddedUexp, vanillaUexp, true);

            LogDebug("[IMPORT] Swapped in modded combo tree, creating fresh parser (no cache)...");

            using var freshParser = new AnimationParser();
            freshParser.Initialize(_gameRootPath, _contentPath);
            return freshParser.LoadMainCharComboTree();
        }
        finally
        {
            try
            {
                var backupUasset = Path.Combine(backupDir, "MainChar_ComboTree.uasset");
                var backupUexp = Path.Combine(backupDir, "MainChar_ComboTree.uexp");

                if (File.Exists(backupUasset))
                    File.Copy(backupUasset, vanillaUasset, true);
                if (File.Exists(backupUexp) && File.Exists(vanillaUexp))
                    File.Copy(backupUexp, vanillaUexp, true);

                LogDebug("[IMPORT] Restored vanilla combo tree from backup");
            }
            catch (Exception ex)
            {
                LogDebug($"[IMPORT] WARNING: Failed to restore vanilla combo tree: {ex.Message}");
                LogDebug($"[IMPORT] Manual restore available at: {backupDir}");
            }
        }
    }

    private ComboGraph? ParseComboTreeFromObject(UObject obj, string weaponName = "BareHands")
    {
        bool isMainChar = string.Equals(weaponName, "BareHands", StringComparison.OrdinalIgnoreCase)
                       || string.Equals(weaponName, "MainChar", StringComparison.OrdinalIgnoreCase)
                       || weaponName.StartsWith("MainChar_", StringComparison.OrdinalIgnoreCase);
        bool isBarehands = string.Equals(weaponName, "BareHands", StringComparison.OrdinalIgnoreCase)
                       || string.Equals(weaponName, "MainChar", StringComparison.OrdinalIgnoreCase)
                       || string.Equals(weaponName, "MainChar_Barehands", StringComparison.OrdinalIgnoreCase);
        try
        {
            LogDebug($"[COMBO] Loaded {obj.GetPathName()}: exportType={obj.ExportType}, weapon={weaponName}");

            var allNodeStructs = new List<FStructFallback>();
            var nodesProp = obj.Properties.FirstOrDefault(p => p.Name.Text == "m_Nodes");
            if (nodesProp?.Tag is ArrayProperty nodesArray)
            {
                foreach (var nodeTag in nodesArray.Value.Properties)
                {
                    if (nodeTag is StructProperty nodeStruct &&
                        nodeStruct.Value.StructType is FStructFallback nodeData)
                        allNodeStructs.Add(nodeData);
                }
            }

            var rawNodes = new List<ComboNode>();
            var treeIndexToNodeIds = new Dictionary<int, List<int>>();
            int nextId = 0;
            for (int i = 0; i < allNodeStructs.Count; i++)
            {
                var variants = ParseComboNodeVariants(allNodeStructs[i], nextId, i);
                treeIndexToNodeIds[i] = new List<int>();
                foreach (var variant in variants)
                {
                    treeIndexToNodeIds[i].Add(variant.Id);
                    rawNodes.Add(variant);
                    nextId++;
                }
                if (variants.Count > 1)
                    LogDebug($"[COMBO] Split tree node {i} into {variants.Count} direction variants: {string.Join(", ", variants.Select(v => $"{v.Name} [{v.DirectionLabel}]"))}");
            }

            LogDebug($"[COMBO] Parsed {rawNodes.Count} raw nodes from {allNodeStructs.Count} tree nodes");

            var rawEdges = new List<ComboEdge>();
            var redirectNodeIds = new HashSet<int>(rawNodes.Where(n => n.IsRedirect).Select(n => n.Id));
            if (redirectNodeIds.Count > 0)
                LogDebug($"[COMBO] Found {redirectNodeIds.Count} redirect nodes: {string.Join(", ", rawNodes.Where(n => n.IsRedirect).Select(n => $"{n.Name} → tree[{n.RedirectTargetTreeIndex}]"))}");
            var conduitIds = new HashSet<int>(
                rawNodes.Where(n => string.IsNullOrEmpty(n.AnimPath) && !n.IsRoot && !n.IsRedirect
                                    && !n.IsActionNode && !n.IsEventNode).Select(n => n.Id));
            var treeRows = new Dictionary<int, List<TreeRowInfo>>();
            for (int ti = 0; ti < allNodeStructs.Count; ti++)
            {
                var nodeData = allNodeStructs[ti];
                var transitionsProp = nodeData.Properties.FirstOrDefault(p => p.Name.Text == "m_Transitions");
                if (transitionsProp?.Tag is not StructProperty transStruct ||
                    transStruct.Value.StructType is not FStructFallback transData)
                    continue;

                var transArr = transData.Properties.FirstOrDefault(p => p.Name.Text == "m_Transitions");
                if (transArr?.Tag is not ArrayProperty tArr)
                    continue;

                var treeNodeRows = new List<TreeRowInfo>();

                var sourceIds = treeIndexToNodeIds[ti];
                var plainSources = sourceIds.Where(id => !redirectNodeIds.Contains(id)).ToList();
                var redirectSources = sourceIds.Where(id => redirectNodeIds.Contains(id)).ToList();

                // A' pass-through: a redirect's own transition rows define the continuation
                // of the chain step whose attack it plays. Re-source them onto that attack
                // node so the graph reads attack -> next-redirect instead of dropping them.
                // The redirect's play target chain (redirect->redirect) is followed once
                // with a cycle guard; an unresolvable/conduit target falls back to the old
                // behavior (rows dropped) with a debug note.
                var reSource = new List<(int redirectId, List<int> fromIds)>();
                foreach (var rId in redirectSources)
                {
                    var rNode = rawNodes.FirstOrDefault(n => n.Id == rId);
                    if (rNode == null) continue;
                    int cur = rNode.RedirectTargetTreeIndex;
                    var visitedTi = new HashSet<int>();
                    List<int>? fromIds = null;
                    while (true)
                    {
                        if (cur < 0 || cur >= allNodeStructs.Count || !treeIndexToNodeIds.ContainsKey(cur)
                            || !visitedTi.Add(cur))
                        {
                            LogDebug($"[COMBO] Redirect tree[{ti}] node[{rId}]: unresolvable play target tree[{cur}] - transitions not re-sourced");
                            break;
                        }
                        var ids = treeIndexToNodeIds[cur];
                        var nextRedirect = ids.FirstOrDefault(id => redirectNodeIds.Contains(id), -1);
                        if (nextRedirect < 0)
                        {
                            fromIds = ids.Any(id => conduitIds.Contains(id)) ? null : ids;
                            if (fromIds == null)
                                LogDebug($"[COMBO] Redirect tree[{ti}] node[{rId}]: play target tree[{cur}] is a conduit - transitions not re-sourced");
                            break;
                        }
                        var nextNode = rawNodes.FirstOrDefault(n => n.Id == nextRedirect);
                        if (nextNode == null) break;
                        cur = nextNode.RedirectTargetTreeIndex;
                    }
                    if (fromIds != null && fromIds.Count > 0)
                        reSource.Add((rId, fromIds));
                }

                int rowIndex = -1;
                foreach (var elem in tArr.Value.Properties)
                {
                    rowIndex++;
                    if (elem is not StructProperty elemStruct ||
                        elemStruct.Value.StructType is not FStructFallback elemData)
                        continue;

                    var inputName = ExtractInputName(elemData);
                    var targetNodesProp = elemData.Properties.FirstOrDefault(p => p.Name.Text == "m_TargetNodes");

                    // Branch-chance snapshot: index-aligned with the asset array (rows
                    // without m_TargetNodes are captured too - export patches by row index).
                    var snapRow = new TreeRowInfo
                    {
                        RowIndex = rowIndex,
                        InputName = inputName,
                        OriginalProbability =
                            elemData.Properties.FirstOrDefault(p => p.Name.Text == "m_fProbability")?.Tag is FloatProperty probFp
                                ? probFp.Value : 100f,
                        ConditionPresent = elemData.Properties.Any(p => p.Name.Text == "m_ConditionInstance"),
                        ConditionClass = ResolveConditionClass(elemData),
                        ConditionInstanceIndex = ResolveConditionInstanceIndex(elemData),
                        ConditionParamList = ResolveConditionParams(elemData)
                    };
                    snapRow.ConditionParams = (snapRow.ConditionParamList ?? new List<ConditionParamInfo>())
                        .Select(pi => pi.Path + " = " + pi.Value).ToList();
                    if (targetNodesProp?.Tag is MapProperty snapMap && snapMap.Value != null)
                    {
                        foreach (var kv in snapMap.Value.Properties)
                        {
                            snapRow.Keys.Add(kv.Key is ByteProperty snapBk ? snapBk.Value
                                : kv.Key is IntProperty snapIk ? (byte)snapIk.Value
                                : (byte)255);
                            snapRow.TargetTreeIndices.Add(kv.Value is IntProperty snapIv ? snapIv.Value : -1);
                        }
                        snapRow.KeyCount = Math.Max(1, snapRow.Keys.Count);
                    }
                    treeNodeRows.Add(snapRow);

                    if (targetNodesProp?.Tag is not MapProperty targetMap)
                        continue;

                    foreach (var kv in targetMap.Value.Properties)
                    {
                        byte targetKey = 255;
                        if (kv.Key is ByteProperty byteKey)
                            targetKey = byteKey.Value;
                        else if (kv.Key is IntProperty intKey)
                            targetKey = (byte)intKey.Value;

                        if (kv.Value is IntProperty intProp)
                        {
                            var targetTreeIndex = intProp.Value;
                            if (targetTreeIndex >= 0 && targetTreeIndex < allNodeStructs.Count &&
                                treeIndexToNodeIds.ContainsKey(targetTreeIndex))
                            {
                                var targetIds = treeIndexToNodeIds[targetTreeIndex];
                                var srcName = rawNodes.FirstOrDefault(n => n.Id == sourceIds.FirstOrDefault())?.Name ?? "?";
                                var tgtName = rawNodes.FirstOrDefault(n => n.Id == targetIds.FirstOrDefault())?.Name ?? "?";
                                if (!isMainChar)
                                    LogDebug($"[REDIRECT DEBUG] Transition: srcTreeIdx={ti}({srcName}) → tgtTreeIdx={targetTreeIndex}({tgtName}) input={inputName}");
                                foreach (var srcId in plainSources)
                                {
                                    foreach (var tgtId in targetIds)
                                    {
                                        rawEdges.Add(new ComboEdge
                                        {
                                            FromNodeId = srcId,
                                            ToNodeId = tgtId,
                                            InputName = inputName
                                        });
                                    }
                                }
                                foreach (var (rId, fromIds) in reSource)
                                {
                                    foreach (var srcId in fromIds)
                                    {
                                        foreach (var tgtId in targetIds)
                                        {
                                            rawEdges.Add(new ComboEdge
                                            {
                                                FromNodeId = srcId,
                                                ToNodeId = tgtId,
                                                InputName = inputName,
                                                OwnerRedirectNodeId = rId
                                            });
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
                if (treeNodeRows.Count > 0) treeRows[ti] = treeNodeRows;
            }

            var treeNodeConditions = new Dictionary<int, TreeNodeConditionInfo>();
            for (int ti = 0; ti < allNodeStructs.Count; ti++)
            {
                var nodeData = allNodeStructs[ti];
                var info = new TreeNodeConditionInfo();

                if (nodeData.Properties.FirstOrDefault(p => p.Name.Text == "m_RequiredTags")?.Tag is StructProperty tagsSt
                    && tagsSt.Value?.StructType is FGameplayTagContainer tagsContainer
                    && tagsContainer.GameplayTags is { Length: > 0 })
                {
                    foreach (var gt in tagsContainer.GameplayTags)
                    {
                        var tagText = gt.TagName.Text;
                        if (!string.IsNullOrEmpty(tagText) && !info.RequiredTags.Contains(tagText))
                            info.RequiredTags.Add(tagText);
                    }
                }

                if (nodeData.Properties.FirstOrDefault(p => p.Name.Text == "m_ConditionalAttacks")?.Tag is MapProperty caMap
                    && caMap.Value != null && caMap.Value.Properties != null)
                {
                    foreach (var kv in caMap.Value.Properties)
                    {
                        var entry = new ConditionalAttackEntry();
                        if (kv.Key is StructProperty kst && kst.Value?.StructType is FStructFallback keyData)
                        {
                            var tagNameProp = keyData.Properties.FirstOrDefault(p => p.Name.Text == "TagName");
                            if (tagNameProp?.Tag is NameProperty tagNp)
                                entry.TagName = tagNp.Value.Text;
                        }
                        if (kv.Value is StructProperty vst && vst.Value?.StructType is FStructFallback valData)
                        {
                            foreach (var ap in valData.Properties)
                            {
                                if (ap.Name.Text != "m_Attacks" || ap.Tag is not NameProperty apn)
                                    continue;
                                var path = apn.Value.Text;
                                if (string.IsNullOrEmpty(path) || path == "None")
                                    continue;
                                var asset = path.Split('/')[^1].Split('.')[0];
                                if (asset.Length > 0 && !entry.AttackNames.Contains(asset))
                                    entry.AttackNames.Add(asset);
                            }
                        }
                        if (entry.TagName.Length > 0 || entry.AttackNames.Count > 0)
                            info.ConditionalAttacks.Add(entry);
                    }
                }

                if (info.RequiredTags.Count > 0 || info.ConditionalAttacks.Count > 0)
                    treeNodeConditions[ti] = info;
            }

            LogDebug($"[COMBO] Parsed {rawEdges.Count} raw edges");

            rawEdges = rawEdges.GroupBy(e => (e.FromNodeId, e.ToNodeId))
                .Select(g => g.OrderByDescending(e => e.OwnerRedirectNodeId >= 0).First()).ToList();
            LogDebug($"[COMBO] After dedup: {rawEdges.Count} raw edges");

            var conduitEdges = new Dictionary<int, List<ComboEdge>>();
            foreach (var cid in conduitIds)
            {
                var targets = new List<ComboEdge>();
                var visited = new HashSet<int>();
                var queue = new Queue<int>();
                queue.Enqueue(cid);
                while (queue.Count > 0)
                {
                    var current = queue.Dequeue();
                    if (!visited.Add(current)) continue;
                    foreach (var e in rawEdges.Where(e => e.FromNodeId == current))
                    {
                        if (conduitIds.Contains(e.ToNodeId))
                            queue.Enqueue(e.ToNodeId);
                        else
                            targets.Add(new ComboEdge
                            {
                                ToNodeId = e.ToNodeId,
                                InputName = e.InputName
                            });
                    }
                }
                conduitEdges[cid] = targets;
            }

            var finalEdges = new List<ComboEdge>();
            foreach (var edge in rawEdges)
            {
                var fromIsConduit = conduitIds.Contains(edge.FromNodeId);
                var toIsConduit = conduitIds.Contains(edge.ToNodeId);
                if (fromIsConduit) continue;

                if (toIsConduit && conduitEdges.TryGetValue(edge.ToNodeId, out var remapTargets))
                {
                    foreach (var remapTarget in remapTargets)
                    {
                        finalEdges.Add(new ComboEdge
                        {
                            FromNodeId = edge.FromNodeId,
                            ToNodeId = remapTarget.ToNodeId,
                            InputName = string.IsNullOrEmpty(edge.InputName) ? remapTarget.InputName : edge.InputName,
                            OwnerRedirectNodeId = edge.OwnerRedirectNodeId
                        });
                    }
                }
                else if (!toIsConduit)
                {
                    finalEdges.Add(new ComboEdge
                    {
                        FromNodeId = edge.FromNodeId,
                        ToNodeId = edge.ToNodeId,
                        InputName = edge.InputName,
                        OwnerRedirectNodeId = edge.OwnerRedirectNodeId
                    });
                }
            }

            var keptNodes = rawNodes.Where(n => !conduitIds.Contains(n.Id)).ToList();
            var oldToNew = new Dictionary<int, int>();
            for (int i = 0; i < keptNodes.Count; i++)
            {
                oldToNew[keptNodes[i].Id] = i;
                keptNodes[i].Id = i;
            }

            string graphWeaponName = !isMainChar
                ? weaponName
                : isBarehands
                    ? "BareHands"
                    : weaponName;
            var graph = new ComboGraph { WeaponName = graphWeaponName, RawNodeCount = allNodeStructs.Count };
            // Conduit routing for display: raw m_Nodes struct index -> the non-conduit
            // nodes its passthrough reached (BFS above already collapsed conduit chains).
            // conduitEdges is keyed in parse node-id space (direction variants shift ids
            // off the struct index), so translate through rawNodes - rows and node
            // TreeIndex both speak struct indices.
            if (rawNodes.Count > 0)
            {
                var idToTree = rawNodes
                    .GroupBy(n => n.Id)
                    .ToDictionary(g => g.Key, g => g.First().TreeIndex);
                foreach (var kv in conduitEdges)
                {
                    if (!idToTree.TryGetValue(kv.Key, out int conduitTree)) continue;
                    var targets = kv.Value
                        .Select(t => t.ToNodeId)
                        .Where(id => idToTree.ContainsKey(id))
                        .Select(id => idToTree[id])
                        .Distinct()
                        .OrderBy(x => x)
                        .ToList();
                    if (targets.Count > 0) graph.ConduitTargets[conduitTree] = targets;
                }
            }
            graph.Nodes.AddRange(keptNodes);
            graph.TreeRows = treeRows;
            graph.TreeNodeConditions = treeNodeConditions;

            foreach (var edge in finalEdges)
            {
                if (oldToNew.TryGetValue(edge.FromNodeId, out var newFrom) &&
                    oldToNew.TryGetValue(edge.ToNodeId, out var newTo))
                {
                    graph.Edges.Add(new ComboEdge
                    {
                        FromNodeId = newFrom,
                        ToNodeId = newTo,
                        InputName = edge.InputName,
                        OwnerRedirectNodeId = edge.OwnerRedirectNodeId >= 0
                            && oldToNew.TryGetValue(edge.OwnerRedirectNodeId, out var newOwner)
                                ? newOwner
                                : -1
                    });
                }
            }

            foreach (var node in graph.Nodes.Where(n => n.IsRedirect))
            {
                if (treeIndexToNodeIds.TryGetValue(node.RedirectTargetTreeIndex, out var targetIds) && targetIds.Count > 0)
                {
                    var resolvedId = targetIds.First();
                    if (oldToNew.TryGetValue(resolvedId, out var newTargetId))
                        node.ResolvedRedirectNodeId = newTargetId;
                }
                graph.RedirectOriginalTargets[node.Id] = node.ResolvedRedirectNodeId;
            }

            // A' pass-through bookkeeping: which redirects had their rows re-sourced (export
            // routes new links from the target attack node back to these).
            foreach (var edge in graph.Edges)
            {
                if (edge.OwnerRedirectNodeId < 0) continue;
                var owner = graph.Nodes.FirstOrDefault(n => n.Id == edge.OwnerRedirectNodeId);
                if (owner != null) owner.OwnedContinuation = true;
            }

            foreach (var edge in graph.Edges)
            {
                var fromNode = graph.Nodes.FirstOrDefault(n => n.Id == edge.FromNodeId);
                if (fromNode?.IsRedirect == true) edge.IsRedirect = true;
            }

            var incomingCounts = new Dictionary<int, int>();
            foreach (var node in graph.Nodes) incomingCounts[node.Id] = 0;
            foreach (var edge in graph.Edges)
                if (incomingCounts.ContainsKey(edge.ToNodeId))
                    incomingCounts[edge.ToNodeId]++;


            LogDebug($"[COMBO] Final: {graph.Nodes.Count} nodes, {graph.Edges.Count} edges (removed {conduitIds.Count} conduits, kept {redirectNodeIds.Count} redirects)");

            var delayAnimNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (isBarehands)
            {
                delayAnimNames.Add("MainChar_Attack_Man_Barehands_Skill_MultiHit_FL");
                delayAnimNames.Add("MainChar_Attack_Man_Barehands_Pressure_TripleHit_BR");
            }

            foreach (var edge in graph.Edges)
            {
                if (string.IsNullOrEmpty(edge.InputName)) continue;
                var targetNode = graph.Nodes.FirstOrDefault(n => n.Id == edge.ToNodeId);
                if (targetNode != null && !string.IsNullOrEmpty(targetNode.AnimPath))
                {
                    var animName = Path.GetFileName(targetNode.AnimPath);
                    bool isDelay = targetNode.AnimPath.Contains("/Delay/", StringComparison.OrdinalIgnoreCase)
                        || delayAnimNames.Contains(animName);
                    if (isDelay)
                        edge.InputName += " Delay";
                }
            }

            var incomingInputs = new Dictionary<int, HashSet<string>>();
            foreach (var edge in graph.Edges)
            {
                if (!string.IsNullOrEmpty(edge.InputName))
                {
                    if (!incomingInputs.ContainsKey(edge.ToNodeId))
                        incomingInputs[edge.ToNodeId] = new HashSet<string>();
                    incomingInputs[edge.ToNodeId].Add(edge.InputName);
                }
            }
            foreach (var node in graph.Nodes)
            {
                if (incomingInputs.TryGetValue(node.Id, out var inputs))
                    node.InputLabel = string.Join(" / ", inputs.OrderBy(x => x));
            }

            if (isBarehands)
            {
                var stanceNode = new ComboNode
                {
                    Id = -1,
                    Name = "MainChar_Stance",
                    DisplayName = "Combat Stance",
                    AnimPath = "Game/Animations/MainChar/Locomotion/Man/Barehands/Moving/V1/Lockmove/North/MC_man_barehands_V1_north_tense",
                    DefaultAnimPath = "Game/Animations/MainChar/Locomotion/Man/Barehands/Moving/V1/Lockmove/North/MC_man_barehands_V1_north_tense",
                    IsRoot = true,
                    Depth = -1
                };

                var existingRootIds = graph.Nodes
                    .Where(n => n.IsRoot || !graph.Edges.Any(e => e.ToNodeId == n.Id))
                    .Select(n => n.Id).ToList();

                foreach (var rootId in existingRootIds)
                    graph.Edges.Add(new ComboEdge { FromNodeId = -1, ToNodeId = rootId, InputName = "" });

                graph.Nodes.Insert(0, stanceNode);
                LogDebug("[COMBO] Injected Combat Stance root (barehands only)");
            }

            ParseStartEntries(obj, graph, treeIndexToNodeIds, oldToNew, rawNodes);

            return graph;
        }
        catch (Exception ex)
        {
            LogDebug($"[COMBO] Error parsing combo tree: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Parses m_ComboStartNodesContainer (AI start-state tickets) into graph.StartEntries.
    /// Entry/transition display only - export re-reads the asset and patches by container index.
    /// </summary>
    private void ParseStartEntries(UObject obj, ComboGraph graph,
        Dictionary<int, List<int>> treeIndexToNodeIds, Dictionary<int, int> oldToNew,
        List<ComboNode> rawNodes)
    {
        try
        {
            var startProp = obj.Properties.FirstOrDefault(p => p.Name.Text == "m_ComboStartNodesContainer");
            if (startProp?.Tag is not ArrayProperty startArr) return;

            int vi = 0;
            foreach (var elemTag in startArr.Value.Properties)
            {
                if (elemTag is not StructProperty entryStruct ||
                    entryStruct.Value.StructType is not FStructFallback entryData)
                {
                    vi++;
                    continue;
                }

                var entry = new StartEntry { VanillaIndex = vi };

                if (entryData.Properties.FirstOrDefault(p => p.Name.Text == "m_EnumState")?.Tag is StructProperty enumState &&
                    enumState.Value.StructType is FStructFallback enumData)
                {
                    entry.TicketEnumValue =
                        (enumData.Properties.FirstOrDefault(p => p.Name.Text == "m_Value")?.Tag as NameProperty)?.Value.Text ?? "";
                }

                if (entryData.Properties.FirstOrDefault(p => p.Name.Text == "m_ComboTransitions")?.Tag is StructProperty comboTrans &&
                    comboTrans.Value.StructType is FStructFallback transData)
                {
                    entry.Label =
                        (transData.Properties.FirstOrDefault(p => p.Name.Text == "m_ParentNodeName")?.Tag as NameProperty)?.Value.Text ?? "";

                    if (transData.Properties.FirstOrDefault(p => p.Name.Text == "m_Transitions")?.Tag is ArrayProperty tArr)
                    {
                        int ti = 0;
                        foreach (var tTag in tArr.Value.Properties)
                        {
                            if (tTag is not StructProperty tStruct ||
                                tStruct.Value.StructType is not FStructFallback tData)
                            {
                                ti++;
                                continue;
                            }

                            var st = new StartTransition { OriginalIndex = ti };

                            if (tData.Properties.FirstOrDefault(p => p.Name.Text == "m_ConditionInstance")?.Tag is ObjectProperty cond &&
                                cond.Value != null)
                            {
                                var resolved = cond.Value.ResolvedObject;
                                if (resolved != null)
                                    entry.ConditionClass = resolved.Class?.Name.Text ?? resolved.Name.Text ?? "";
                            }

                            if (tData.Properties.FirstOrDefault(p => p.Name.Text == "m_fProbability")?.Tag is FloatProperty fp)
                            {
                                st.Probability = fp.Value;
                                st.OriginalProbability = fp.Value;
                            }

                            if (tData.Properties.FirstOrDefault(p => p.Name.Text == "m_TargetNodes")?.Tag is MapProperty mp)
                            {
                                int kc = 0;
                                var keyParts = new List<string>();
                                foreach (var kv in mp.Value.Properties)
                                {
                                    byte key = kv.Key is ByteProperty bk ? bk.Value
                                        : kv.Key is IntProperty ik ? (byte)ik.Value
                                        : (byte)255;
                                    if (kc == 0)
                                        st.KeyByte = key;
                                    int val = kv.Value is IntProperty iv ? iv.Value : -1;
                                    if (val >= 0 && st.TargetTreeIndex < 0)
                                        st.TargetTreeIndex = val;
                                    st.Keys.Add(key);
                                    st.KeyTargetTreeIndices.Add(val);
                                    keyParts.Add($"{key}→{val}");
                                    kc++;
                                }
                                st.KeyCount = Math.Max(1, kc);
                                if (st.KeyCount > 1)
                                    st.TargetSummary = string.Join(", ", keyParts);
                            }

                            // Per-key resolution: each key's target -> graph node id (or the
                            // kept tree index for condensed/conduit targets).
                            for (int ki = 0; ki < st.KeyTargetTreeIndices.Count; ki++)
                            {
                                int val = st.KeyTargetTreeIndices[ki];
                                int id = -1;
                                string nm = "";
                                if (val >= 0 && treeIndexToNodeIds.TryGetValue(val, out var keyIds) && keyIds.Count > 0)
                                {
                                    int rawId = keyIds[0];
                                    if (oldToNew.TryGetValue(rawId, out var keyGid))
                                    {
                                        id = keyGid;
                                        nm = graph.Nodes.FirstOrDefault(n => n.Id == keyGid)?.Name ?? "";
                                    }
                                    else
                                    {
                                        // Conduit / condensed node: not a graph node, keep the tree index.
                                        nm = rawNodes.FirstOrDefault(n => n.Id == rawId)?.Name ?? "";
                                    }
                                }
                                st.KeyTargetNodeIds.Add(id);
                                st.KeyTargetNames.Add(nm);
                            }

                            if (st.TargetTreeIndex >= 0 &&
                                treeIndexToNodeIds.TryGetValue(st.TargetTreeIndex, out var ids) && ids.Count > 0)
                            {
                                int rawId = ids[0];
                                if (oldToNew.TryGetValue(rawId, out var gid))
                                {
                                    st.TargetNodeId = gid;
                                    st.TargetName = graph.Nodes.FirstOrDefault(n => n.Id == gid)?.Name ?? "";
                                }
                                else
                                {
                                    // Conduit / condensed node: not a graph node, keep the tree index.
                                    st.TargetName = rawNodes.FirstOrDefault(n => n.Id == rawId)?.Name ?? "";
                                }
                            }

                            st.OriginalTargetNodeId = st.TargetNodeId;
                            st.OriginalTargetTreeIndex = st.TargetTreeIndex;
                            st.OriginalTargetName = st.TargetName;
                            st.OriginalKeyTargetTreeIndices = new List<int>(st.KeyTargetTreeIndices);
                            entry.Transitions.Add(st);
                            ti++;
                        }
                    }
                }

                entry.PhaseName =
                    (entryData.Properties.FirstOrDefault(p => p.Name.Text == "m_PhaseName")?.Tag as NameProperty)?.Value.Text ?? "";
                if (entryData.Properties.FirstOrDefault(p => p.Name.Text == "m_uiMCDominationFlags")?.Tag is UInt32Property uf)
                    entry.MCDominationFlags = uf.Value;

                graph.StartEntries.Add(entry);
                vi++;
            }

            if (graph.StartEntries.Count > 0)
                LogDebug($"[COMBO] Parsed {graph.StartEntries.Count} start entries: " +
                         string.Join(", ", graph.StartEntries.Select(e =>
                             $"{e.Label}[{e.TicketEnumValue}::{e.Transitions.Count}]")));
        }
        catch (Exception ex)
        {
            LogDebug($"[COMBO] Start entries parse failed: {ex.Message}");
        }
    }

    private static readonly HashSet<string> DirectionSuffixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "FL", "FR", "BL", "BR", "F", "B", "L", "R"
    };

    private static string ExtractDirectionLabel(string dbPath)
    {
        var fileName = Path.GetFileNameWithoutExtension(dbPath);
        var lastUnderscore = fileName.LastIndexOf('_');
        if (lastUnderscore < 0) return "";
        var suffix = fileName[(lastUnderscore + 1)..];
        return DirectionSuffixes.Contains(suffix) ? suffix.ToUpperInvariant() : "";
    }

    public string? ResolveAnimationFromDB(string dbPath)
    {
        try
        {
            var dbObj = _provider?.SafeLoadPackageObject<UObject>(dbPath);
            if (dbObj == null && _overlayProvider != null)
                dbObj = _overlayProvider.SafeLoadPackageObject<UObject>(dbPath);
            if (dbObj == null)
            {
                LogDebug($"[DBRESOLVE] {dbPath}: package not found" +
                         (_overlayProvider == null ? " (no overlay mounted)" : " (vanilla + overlay)"));
                return null;
            }

            var mAttack = dbObj.Properties.FirstOrDefault(p => p.Name.Text == "m_Attack");
            if (mAttack?.Tag is StructProperty attackStruct &&
                attackStruct.Value.StructType is FStructFallback attackData)
            {
                var mAnimation = attackData.Properties.FirstOrDefault(p => p.Name.Text == "m_Animation");
                if (mAnimation?.Tag is ObjectProperty animObj && animObj.Value != null)
                {
                    var resolved = animObj.Value.ResolvedObject;
                    if (resolved != null)
                    {
                        var fullPath = resolved.GetPathName();
                        if (!string.IsNullOrEmpty(fullPath) && fullPath != "None")
                            return NormalizeAnimPath(fullPath);
                        LogDebug($"[DBRESOLVE] {dbPath}: m_Animation resolved to empty path");
                    }
                    else
                    {
                        LogDebug($"[DBRESOLVE] {dbPath}: m_Animation ResolvedObject is null (lazy reference not in any provider)");
                    }
                }
                else
                {
                    LogDebug($"[DBRESOLVE] {dbPath}: m_Animation missing or not an ObjectProperty");
                }
            }
            else
            {
                LogDebug($"[DBRESOLVE] {dbPath}: m_Attack missing or not a struct" +
                         (mAttack == null ? "" : $" (tag={mAttack.Tag?.GetType().Name})"));
            }
        }
        catch (Exception ex)
        {
            LogDebug($"[DBRESOLVE] {dbPath}: exception {ex.GetType().Name}: {ex.Message}");
        }
        return null;
    }

    public string DumpAttackDBProperties(string dbPath)
    {
        try
        {
            var dbObj = _provider?.SafeLoadPackageObject<UObject>(dbPath);
            if (dbObj == null) return "(DB entry not found)";

            var mAttack = dbObj.Properties.FirstOrDefault(p => p.Name.Text == "m_Attack");
            if (mAttack?.Tag is StructProperty attackStruct &&
                attackStruct.Value.StructType is FStructFallback attackData)
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"Properties ({attackData.Properties.Count}):");
                foreach (var prop in attackData.Properties)
                {
                    var typeName = prop.PropertyType.Text;
                    var val = prop.Tag?.ToString() ?? "?";
                    sb.AppendLine($"  {prop.Name.Text} [{typeName}]: {val}");
                }
                return sb.ToString();
            }
            return "(m_Attack struct not found)";
        }
        catch (Exception ex)
        {
            return $"(error: {ex.Message})";
        }
    }

    private List<ComboNode> ParseComboNodeVariants(FStructFallback nodeStruct, int nextId, int treeIndex)
    {
        var nameProp = nodeStruct.Properties.FirstOrDefault(p => p.Name.Text == "m_Name");
        var nodeName = nameProp?.Tag is NameProperty np ? np.Value.Text : "";
        var isRoot = nodeName.Contains("Conduit", StringComparison.OrdinalIgnoreCase) ||
                     nodeName.Contains("Root", StringComparison.OrdinalIgnoreCase);

        var baseDisplayName = nodeName;
        if (string.IsNullOrEmpty(nodeName) || nodeName == "None")
        {
            var transStruct = nodeStruct.Properties.FirstOrDefault(p => p.Name.Text == "m_Transitions")?.Tag as StructProperty;
            var transFallback = transStruct?.Value?.StructType as FStructFallback;
            var parentName = transFallback?.Properties
                .FirstOrDefault(p => p.Name.Text == "m_ParentNodeName")?.Tag as NameProperty;
            var pl = parentName?.Value.Text ?? "";
            var clean = new System.Text.StringBuilder(pl.Length);
            foreach (char c in pl) if (!char.IsControl(c)) clean.Append(c);
            baseDisplayName = clean.ToString().Trim();
            if (baseDisplayName.Length == 0) baseDisplayName = $"Node_{treeIndex}";
        }
        else
            baseDisplayName = nodeName
                .Replace("MainChar_", "")
                .Replace("_", " ")
                .Replace("Attack barehands ", "")
                .Replace("attack barehands ", "");

        var attacks = new List<(string dbPath, string animPath)>();
        var attackInfosProp = nodeStruct.Properties.FirstOrDefault(p => p.Name.Text == "m_AttackInfos");
        if (attackInfosProp?.Tag is StructProperty attackInfosStruct &&
            attackInfosStruct.Value.StructType is FStructFallback attackInfosData)
        {
            foreach (var ap in attackInfosData.Properties)
            {
                if (ap.Name.Text == "m_Attacks" && ap.Tag is NameProperty apName &&
                    apName.Value.Text != "None" && apName.Value.Text.Contains("/"))
                {
                    var db = NormalizeAnimPath(apName.Value.Text);
                    var anim = ResolveAnimationFromDB(db) ?? "";
                    attacks.Add((db, anim));
                }
            }
        }

        attacks = attacks.GroupBy(a => a.dbPath).Select(g => g.First()).ToList();

        if (nodeName.Contains("FarGrab", StringComparison.OrdinalIgnoreCase) ||
            nodeName.Contains("GrabThrow", StringComparison.OrdinalIgnoreCase) ||
            attacks.Any(a => a.dbPath.Contains("Grab", StringComparison.OrdinalIgnoreCase)))
        {
            LogDebug($"[GRAB_NODE] tree={treeIndex} name='{nodeName}' attacks={attacks.Count} " +
                     $"redirect={(nodeStruct.Properties.FirstOrDefault(p => p.Name.Text == "m_NodeRedirect")?.Tag is IntProperty rp ? rp.Value : "no-prop")} " +
                     $"paths=[{string.Join(", ", attacks.Select(a => $"{a.dbPath} anim={(string.IsNullOrEmpty(a.animPath) ? "EMPTY" : a.animPath)}"))}]");
        }

        var distinctDirs = attacks
            .Select(a => ExtractDirectionLabel(a.dbPath))
            .Where(d => !string.IsNullOrEmpty(d))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        bool showDirLabels = distinctDirs.Count > 1;

        int redirectTarget = -1;
        var redirectProp = nodeStruct.Properties.FirstOrDefault(p => p.Name.Text == "m_NodeRedirect");
        if (redirectProp?.Tag is IntProperty redirectInt && redirectInt.Value >= 0)
            redirectTarget = redirectInt.Value;

        bool isActionNode = false, isEventNode = false;
        string actionClass = "", eventName = "";
        try
        {
            if (nodeStruct.Properties.FirstOrDefault(p => p.Name.Text == "m_AIAttackAction")?.Tag is StructProperty actionSp &&
                actionSp.Value?.StructType is FStructFallback actionData)
            {
                var classProp = actionData.Properties.FirstOrDefault(p => p.Name.Text == "m_DefaultClassObject")?.Tag as ObjectProperty;
                var resolved = classProp?.Value?.ResolvedObject;
                if (resolved != null)
                {
                    isActionNode = true;
                    actionClass = resolved.Name.Text ?? "";
                    if (actionClass.EndsWith("_C", StringComparison.Ordinal)) actionClass = actionClass[..^2];
                }
            }
            if (nodeStruct.Properties.FirstOrDefault(p => p.Name.Text == "m_Event")?.Tag is ObjectProperty eventProp)
            {
                var resolved = eventProp.Value?.ResolvedObject;
                if (resolved != null)
                {
                    isEventNode = true;
                    eventName = resolved.Name.Text ?? "";
                    if (eventName.EndsWith("_C", StringComparison.Ordinal)) eventName = eventName[..^2];
                }
            }
        }
        catch (Exception ex)
        {
            LogDebug($"[COMBO] tree={treeIndex} action/event resolve failed: {ex.Message}");
        }

        if (attacks.Count == 0 || redirectTarget >= 0)
        {
            return new List<ComboNode>
            {
                new ComboNode
                {
                    Id = nextId,
                    TreeIndex = treeIndex,
                    Name = nodeName == "None" ? "" : nodeName,
                    AnimPath = "",
                    DefaultAnimPath = "",
                    DefaultDBPath = "",
                    DisplayName = baseDisplayName,
                    IsRoot = isRoot,
                    DirectionLabel = "",
                    IsRedirect = redirectTarget >= 0,
                    RedirectTargetTreeIndex = redirectTarget,
                    IsActionNode = isActionNode,
                    ActionClass = actionClass,
                    IsEventNode = isEventNode,
                    EventName = eventName,
                }
            };
        }

        var variants = new List<ComboNode>();
        int id = nextId;
        foreach (var (db, anim) in attacks)
        {
            var dir = ExtractDirectionLabel(db);
            var displayName = baseDisplayName;
            if (showDirLabels && !string.IsNullOrEmpty(dir) && !displayName.EndsWith(dir, StringComparison.OrdinalIgnoreCase))
                displayName = $"{baseDisplayName} {dir}";

            variants.Add(new ComboNode
            {
                Id = id++,
                TreeIndex = treeIndex,
                Name = nodeName == "None" ? "" : nodeName,
                AnimPath = anim,
                DefaultAnimPath = anim,
                DefaultDBPath = db,
                SourceDBPath = db,
                DisplayName = displayName,
                IsRoot = isRoot,
                DirectionLabel = dir,
                IsActionNode = isActionNode,
                ActionClass = actionClass,
                IsEventNode = isEventNode,
                EventName = eventName,
            });
        }

        return variants;
    }

    public List<ModdedComboNodeInfo> ReadModdedComboNodes(string uassetPath, string? contentRoot = null)
    {
        var result = new List<ModdedComboNodeInfo>();
        try
        {
            var asset = new UAsset(uassetPath, UAssetAPI.UnrealTypes.EngineVersion.VER_UE4_26, null, CustomSerializationFlags.None);
            string contentRel = "";
            if (!string.IsNullOrEmpty(contentRoot))
            {
                try
                {
                    contentRel = Path.GetRelativePath(contentRoot, uassetPath).Replace('\\', '/');
                    if (contentRel.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase))
                        contentRel = contentRel[..^7];
                }
                catch { contentRel = ""; }
            }
            ArrayPropertyData? nodesArr = null;
            foreach (var exp in asset.Exports)
            {
                if (exp is not UAssetAPI.ExportTypes.NormalExport ne || ne.Data == null) continue;
                nodesArr = ne.Data.OfType<ArrayPropertyData>()
                    .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_Nodes");
                if (nodesArr != null) break;
            }
            if (nodesArr?.Value == null)
            {
                LogDebug($"[IMPORT] ReadModdedComboNodes: no m_Nodes in {uassetPath}");
                return result;
            }

            var mapOverrides = BuildAttackMapOverrides(asset);

            for (int i = 0; i < nodesArr.Value.Length; i++)
            {
                if (nodesArr.Value[i] is not StructPropertyData nodeSp || nodeSp.Value == null) continue;

                var info = new ModdedComboNodeInfo { TreeIndex = i, ModdedContentRel = contentRel };
                var nameProp = nodeSp.Value.OfType<NamePropertyData>()
                    .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_Name");
                info.Name = nameProp?.Value?.Value?.ToString() ?? "";
                if (info.Name == "None") info.Name = "";

                var redirectProp = nodeSp.Value.OfType<IntPropertyData>()
                    .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_NodeRedirect");
                if (redirectProp != null && redirectProp.Value >= 0)
                    info.RedirectTargetTreeIndex = redirectProp.Value;

                // AI payload: prefer the action class, fall back to the event object/class
                // name - same precedence the vanilla parser uses for node badges.
                var actionClass = ActionNodeCatalog.ActionClassName(asset, nodeSp);
                if (!string.IsNullOrEmpty(actionClass))
                {
                    info.AiKind = "Action";
                    info.AiClass = actionClass;
                }
                else
                {
                    var evName = ActionNodeCatalog.EventClassName(asset, nodeSp);
                    if (!string.IsNullOrEmpty(evName))
                    {
                        info.AiKind = "Event";
                        info.AiClass = evName;
                    }
                }

                var attackInfos = nodeSp.Value.OfType<StructPropertyData>()
                    .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_AttackInfos");
                if (attackInfos?.Value != null)
                {
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var ap in attackInfos.Value)
                    {
                        if (ap.Name?.Value?.ToString() != "m_Attacks" || ap is not NamePropertyData npd)
                            continue;
                        var path = npd.Value?.Value?.ToString() ?? "";
                        if (string.IsNullOrEmpty(path) || path == "None" || !path.Contains('/'))
                            continue;
                        var norm = NormalizeAnimPath(path);
                        if (mapOverrides.TryGetValue(norm, out var resolved) &&
                            !string.Equals(resolved, norm, StringComparison.OrdinalIgnoreCase))
                        {
                            norm = resolved;
                        }
                        if (seen.Add(norm))
                            info.AttackDbPaths.Add(norm);
                    }
                }

                // Transitions: nested m_Transitions struct -> array of { m_eInputTransition,
                // m_fProbability, m_ConditionInstance, m_TargetNodes map } (mirrors
                // ParseComboTreeFromObject, but on PropertyData). Rows are snapshotted
                // index-aligned for the chance/condition diff; the flat Transitions list
                // keeps the per-key shape the edge-surfacing pass consumes.
                var transStructProp = nodeSp.Value.OfType<StructPropertyData>()
                    .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_Transitions");
                var transArrProp = transStructProp?.Value?.OfType<ArrayPropertyData>()
                    .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_Transitions");
                if (transArrProp?.Value != null)
                {
                    int rowIndex = -1;
                    foreach (var tElem in transArrProp.Value)
                    {
                        rowIndex++;
                        if (tElem is not StructPropertyData tSp || tSp.Value == null) continue;
                        var inputProp = tSp.Value.OfType<EnumPropertyData>()
                            .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_eInputTransition");
                        string inputName = InputNameFromEnumRaw(inputProp?.Value?.Value?.ToString());

                        var row = new ModdedTransitionRowInfo { RowIndex = rowIndex, InputName = inputName };
                        var probProp = tSp.Value.OfType<FloatPropertyData>()
                            .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_fProbability");
                        row.Probability = probProp?.Value ?? 100f;

                        var condProp = tSp.Value.OfType<ObjectPropertyData>()
                            .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_ConditionInstance");
                        int condIdx = condProp?.Value?.Index ?? 0;
                        if (condIdx > 0 && condIdx <= asset.Exports.Count)
                        {
                            row.ConditionInstanceExport = condIdx;
                            row.ConditionClass = ConditionCatalog.ExportClassName(asset, condIdx - 1);
                            if (asset.Exports[condIdx - 1] is UAssetAPI.ExportTypes.NormalExport condExp &&
                                condExp.Data != null)
                            {
                                row.ConditionParams = ConditionCatalog.CollectParams(asset, condExp.Data);
                            }
                        }
                        else if (condIdx < 0)
                        {
                            row.ConditionClass = ActionNodeCatalog.ResolveObjectName(asset, condProp?.Value) ?? "";
                        }

                        var targetsProp = tSp.Value.OfType<MapPropertyData>()
                            .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_TargetNodes");
                        if (targetsProp?.Value != null)
                        {
                            try
                            {
                                foreach (System.Collections.DictionaryEntry kv in (System.Collections.IDictionary)targetsProp.Value)
                                {
                                    byte key = kv.Key is BytePropertyData bkpd ? bkpd.Value
                                        : kv.Key is IntPropertyData ikpd ? (byte)ikpd.Value
                                        : (byte)255;
                                    int targetTreeIndex = kv.Value switch
                                    {
                                        IntPropertyData ipd => ipd.Value,
                                        BytePropertyData bpd => bpd.Value,
                                        _ => -1
                                    };
                                    row.Keys.Add(key);
                                    row.TargetTreeIndices.Add(targetTreeIndex);
                                    if (targetTreeIndex < 0) continue;
                                    info.Transitions.Add(new ModdedTransitionInfo
                                    {
                                        InputName = inputName,
                                        TargetTreeIndex = targetTreeIndex
                                    });
                                }
                            }
                            catch (Exception tEx)
                            {
                                LogDebug($"[IMPORT] Transition target read failed at tree[{info.TreeIndex}]: {tEx.Message}");
                            }
                        }
                        info.TransitionRows.Add(row);
                    }
                }

                result.Add(info);
            }

            LogDebug($"[IMPORT] ReadModdedComboNodes: {result.Count} nodes, {mapOverrides.Count} map override(s) from {Path.GetFileName(uassetPath)}");
        }
        catch (Exception ex)
        {
            LogDebug($"[IMPORT] ReadModdedComboNodes failed for {uassetPath}: {ex.Message}");
        }
        return result;
    }

    /// <summary>
    /// Reads m_ComboStartNodesContainer from a modded combo file (UAssetAPI walk
    /// mirroring ParseStartEntries) so the import diff can overwrite probabilities and
    /// targets on vanilla-matched entries. Structural adds/removes are the caller's
    /// log-only concern.
    /// </summary>
    public List<ModdedStartEntryInfo> ReadModdedStartEntries(string uassetPath)
    {
        var result = new List<ModdedStartEntryInfo>();
        try
        {
            var asset = new UAsset(uassetPath, UAssetAPI.UnrealTypes.EngineVersion.VER_UE4_26, null, CustomSerializationFlags.None);
            ArrayPropertyData? cont = null;
            foreach (var exp in asset.Exports)
            {
                if (exp is not UAssetAPI.ExportTypes.NormalExport ne || ne.Data == null) continue;
                cont = ne.Data.OfType<ArrayPropertyData>()
                    .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_ComboStartNodesContainer");
                if (cont != null) break;
            }
            if (cont?.Value == null) return result;

            foreach (var elem in cont.Value)
            {
                if (elem is not StructPropertyData en || en.Value == null) continue;
                var entry = new ModdedStartEntryInfo();

                var enumState = en.Value.OfType<StructPropertyData>()
                    .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_EnumState");
                entry.TicketEnumValue = enumState?.Value?.OfType<NamePropertyData>()
                    .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_Value")?.Value?.Value?.ToString() ?? "";

                var transStruct = en.Value.OfType<StructPropertyData>()
                    .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_ComboTransitions");
                entry.Label = transStruct?.Value?.OfType<NamePropertyData>()
                    .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_ParentNodeName")?.Value?.Value?.ToString() ?? "";

                var rows = transStruct?.Value?.OfType<ArrayPropertyData>()
                    .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_Transitions");
                if (rows?.Value != null)
                {
                    foreach (var rElem in rows.Value)
                    {
                        if (rElem is not StructPropertyData rSp || rSp.Value == null) continue;
                        var row = new ModdedStartTransitionInfo();
                        var fp = rSp.Value.OfType<FloatPropertyData>()
                            .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_fProbability");
                        row.Probability = fp?.Value ?? 100f;

                        var cond = rSp.Value.OfType<ObjectPropertyData>()
                            .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_ConditionInstance");
                        int cIdx = cond?.Value?.Index ?? 0;
                        if (cIdx > 0 && cIdx <= asset.Exports.Count)
                            row.ConditionClass = ConditionCatalog.ExportClassName(asset, cIdx - 1);
                        else if (cIdx < 0)
                            row.ConditionClass = ActionNodeCatalog.ResolveObjectName(asset, cond?.Value) ?? "";

                        var map = rSp.Value.OfType<MapPropertyData>()
                            .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_TargetNodes");
                        if (map?.Value != null)
                        {
                            try
                            {
                                foreach (System.Collections.DictionaryEntry kv in (System.Collections.IDictionary)map.Value)
                                {
                                    byte key = kv.Key is BytePropertyData bkpd ? bkpd.Value
                                        : kv.Key is IntPropertyData ikpd ? (byte)ikpd.Value
                                        : (byte)255;
                                    int val = kv.Value is IntPropertyData ivpd ? ivpd.Value : -1;
                                    row.Keys.Add(key);
                                    row.KeyTargetTreeIndices.Add(val);
                                }
                            }
                            catch (Exception rEx)
                            {
                                LogDebug($"[IMPORT] Start-entry target read failed: {rEx.Message}");
                            }
                        }

                        if (string.IsNullOrEmpty(entry.ConditionClass) && !string.IsNullOrEmpty(row.ConditionClass))
                            entry.ConditionClass = row.ConditionClass;
                        entry.Transitions.Add(row);
                    }
                }

                entry.PhaseName = en.Value.OfType<NamePropertyData>()
                    .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_PhaseName")?.Value?.Value?.ToString() ?? "";
                var flags = en.Value.OfType<UInt32PropertyData>()
                    .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_uiMCDominationFlags");
                if (flags != null)
                    entry.MCDominationFlags = flags.Value;
                result.Add(entry);
            }

            LogDebug($"[IMPORT] ReadModdedStartEntries: {result.Count} entries from {Path.GetFileName(uassetPath)}");
        }
        catch (Exception ex)
        {
            LogDebug($"[IMPORT] ReadModdedStartEntries failed for {uassetPath}: {ex.Message}");
        }
        return result;
    }

    private static Dictionary<string, string> BuildAttackMapOverrides(UAsset asset)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var exp in asset.Exports)
        {
            if (exp is not UAssetAPI.ExportTypes.NormalExport ne || ne.Data == null) continue;
            foreach (var map in FindAllMapsNamed(ne.Data, "m_Attacks"))
            {
                if (map.Value == null) continue;
                try
                {
                    foreach (var kvp in map.Value)
                    {
                        if (kvp.Value is not ObjectPropertyData op || op.Value == null) continue;
                        var keyPath = GetPropertyKeyString(kvp.Key);
                        if (string.IsNullOrEmpty(keyPath) || !keyPath.Contains('/')) continue;
                        var resolved = ResolveImportObjectPath(asset, op.Value.Index);
                        if (string.IsNullOrEmpty(resolved)) continue;
                        result[NormalizeAnimPath(keyPath)] = NormalizeAnimPath(resolved);
                    }
                }
                catch { }
            }
        }
        return result;
    }

    private static string GetPropertyKeyString(PropertyData key)
    {
        if (key is StrPropertyData sp) return sp.Value?.ToString() ?? "";
        if (key is NamePropertyData np) return np.Value?.Value?.ToString() ?? "";
        return "";
    }

    private static string? ResolveImportObjectPath(UAsset asset, int index)
    {
        if (index > 0)
        {
            int exportIdx = index - 1;
            if (exportIdx >= 0 && exportIdx < asset.Exports.Count)
            {
                var exp = asset.Exports[exportIdx];
                string expName = exp.ObjectName?.Value?.ToString() ?? "";
                int expOuterRaw = exp.OuterIndex?.Index ?? 0;
                if (expOuterRaw < 0)
                {
                    int expOuterIdx = -expOuterRaw - 1;
                    if (expOuterIdx >= 0 && expOuterIdx < asset.Exports.Count)
                    {
                        string outerName = asset.Exports[expOuterIdx].ObjectName?.Value?.ToString() ?? "";
                        if (!string.IsNullOrEmpty(outerName))
                            return outerName.EndsWith("." + expName, StringComparison.OrdinalIgnoreCase) ||
                                   outerName.EndsWith("/" + expName, StringComparison.OrdinalIgnoreCase)
                                ? outerName
                                : outerName + "." + expName;
                    }
                    if (expOuterIdx >= 0 && expOuterIdx < asset.Imports.Count)
                    {
                        string pkgPath = asset.Imports[expOuterIdx].ObjectName?.Value?.ToString() ?? "";
                        if (!string.IsNullOrEmpty(pkgPath))
                            return pkgPath.EndsWith("." + expName, StringComparison.OrdinalIgnoreCase) ||
                                   pkgPath.EndsWith("/" + expName, StringComparison.OrdinalIgnoreCase)
                                ? pkgPath
                                : pkgPath + "." + expName;
                    }
                }
                return string.IsNullOrEmpty(expName) ? null : expName;
            }
            return null;
        }
        if (index >= 0) return null;
        int importIdx = -index - 1;
        if (importIdx < 0 || importIdx >= asset.Imports.Count) return null;
        var imp = asset.Imports[importIdx];
        string objName = imp.ObjectName?.Value?.ToString() ?? "";
        int outerRaw = imp.OuterIndex?.Index ?? 0;
        if (outerRaw < 0)
        {
            int outerIdx = -outerRaw - 1;
            if (outerIdx >= 0 && outerIdx < asset.Imports.Count)
            {
                string pkgPath = asset.Imports[outerIdx].ObjectName?.Value?.ToString() ?? "";
                if (!string.IsNullOrEmpty(pkgPath))
                {
                    if (string.IsNullOrEmpty(objName)) return pkgPath;
                    if (pkgPath.EndsWith("." + objName, StringComparison.OrdinalIgnoreCase) ||
                        pkgPath.EndsWith("/" + objName, StringComparison.OrdinalIgnoreCase))
                        return pkgPath;
                    return pkgPath + "." + objName;
                }
            }
        }
        return string.IsNullOrEmpty(objName) ? null : objName;
    }

    private static List<MapPropertyData> FindAllMapsNamed(
        List<PropertyData> props, string mapName)
    {
        var results = new List<MapPropertyData>();
        foreach (var p in props)
        {
            if (p is MapPropertyData mp &&
                p.Name?.Value?.ToString() == mapName)
                results.Add(mp);
            RecurseFindMapsNamed(p, mapName, results);
        }
        return results;
    }

    private static void RecurseFindMapsNamed(
        PropertyData p, string mapName,
        List<MapPropertyData> results)
    {
        if (p is StructPropertyData sp && sp.Value != null)
        {
            foreach (var child in sp.Value)
            {
                if (child is MapPropertyData mp &&
                    child.Name?.Value?.ToString() == mapName)
                    results.Add(mp);
                RecurseFindMapsNamed(child, mapName, results);
            }
        }
        else if (p is ArrayPropertyData ap && ap.Value != null)
        {
            foreach (var elem in ap.Value)
            {
                if (elem is MapPropertyData mp &&
                    elem.Name?.Value?.ToString() == mapName)
                    results.Add(mp);
                RecurseFindMapsNamed(elem, mapName, results);
            }
        }
    }

    private static string NormalizeNodeKey(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name == "None") return "";
        var s = name.Trim();
        foreach (var prefix in new[]
                 {
                     "MainChar_Attack_barehands_", "MainChar_attack_barehands_",
                     "MainChar_Attack_", "MainChar_attack_", "MainChar_",
                     "FireDisciple_attack_barehands_", "FireDisciple_Attack_barehands_",
                     "FireDisciple_barehands_", "FireDisciple_", "Grunt_",
                     "MC_", "Attack barehands ", "attack barehands "
                 })
        {
            if (s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                s = s[prefix.Length..];
                break;
            }
        }
        s = s.Replace("_", " ").Replace("-", " ").Trim();
        while (s.Contains("  "))
            s = s.Replace("  ", " ");
        return s;
    }

    private static string StripDirectionSuffix(string fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return "";
        var lastUnderscore = fileName.LastIndexOf('_');
        if (lastUnderscore <= 0) return fileName;
        var suffix = fileName[(lastUnderscore + 1)..];
        return DirectionSuffixes.Contains(suffix)
            ? fileName[..lastUnderscore]
            : fileName;
    }

    private static string PrettifyAttackName(string dbPath, string directionLabel)
    {
        var fileName = Path.GetFileNameWithoutExtension(dbPath);
        if (string.IsNullOrEmpty(fileName)) return "";
        var display = fileName
            .Replace("MainChar_", "")
            .Replace("FireDisciple_", "")
            .Replace("FireDisciple", "")
            .Replace("Grunt_", "")
            .Replace("MC_", "")
            .Replace("_", " ")
            .Replace("Attack barehands ", "")
            .Replace("Attack Barehands ", "")
            .Replace("attack barehands ", "")
            .Replace("barehands ", "")
            .Replace("Barehands ", "")
            .Trim();
        while (display.Contains("  "))
            display = display.Replace("  ", " ");
        if (!string.IsNullOrEmpty(directionLabel) &&
            !display.EndsWith(directionLabel, StringComparison.OrdinalIgnoreCase))
            display = $"{display} {directionLabel}";
        return display;
    }

    public static string PrettifyNodeName(string dbPath) =>
        PrettifyAttackName(dbPath, ExtractDirectionLabel(dbPath));

    public ModdedApplyResult ApplyModdedComboToVanilla(
        ComboGraph vanillaGraph, List<ModdedComboNodeInfo> moddedNodes, string weaponName,
        List<ModdedStartEntryInfo>? moddedStartEntries = null)
    {
        bool isMainChar = string.Equals(weaponName, "BareHands", StringComparison.OrdinalIgnoreCase)
                       || string.Equals(weaponName, "MainChar", StringComparison.OrdinalIgnoreCase)
                       || weaponName.StartsWith("MainChar_", StringComparison.OrdinalIgnoreCase);

        int moveChanges = 0, retargetChanges = 0, unmatched = 0;
        var result = new ModdedApplyResult();

        var vanillaMoves = vanillaGraph.Nodes
            .Where(n => n.TreeIndex >= 0 && !n.IsRedirect && !n.IsRoot)
            .GroupBy(n => n.TreeIndex)
            .ToDictionary(g => g.Key, g => g.OrderBy(n => n.Id).ToList());

        int vanillaTreeCount = vanillaGraph.Nodes
            .Where(n => n.TreeIndex >= 0)
            .Select(n => n.TreeIndex)
            .Distinct()
            .Count();
        if (moddedNodes.Count != vanillaTreeCount)
            LogDebug($"[IMPORT] Tree size mismatch: {moddedNodes.Count} modded vs {vanillaTreeCount} vanilla tree indices ({weaponName})");

        var usedModded = new HashSet<ModdedComboNodeInfo>();
        var groupToModded = new Dictionary<int, ModdedComboNodeInfo>();

        var byName = new Dictionary<string, Queue<ModdedComboNodeInfo>>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in moddedNodes)
        {
            var key = NormalizeNodeKey(m.Name);
            if (string.IsNullOrEmpty(key)) continue;
            if (!byName.TryGetValue(key, out var q))
                byName[key] = q = new Queue<ModdedComboNodeInfo>();
            q.Enqueue(m);
        }

        foreach (var kvp in vanillaMoves.OrderBy(k => k.Key))
        {
            var nameKey = NormalizeNodeKey(kvp.Value
                .Select(n => n.Name)
                .FirstOrDefault(s => !string.IsNullOrEmpty(s) && s != "None") ?? "");
            if (string.IsNullOrEmpty(nameKey)) continue;
            if (!byName.TryGetValue(nameKey, out var q) || q.Count == 0) continue;
            var match = q.Dequeue();
            groupToModded[kvp.Key] = match;
            usedModded.Add(match);
        }

        var byIndex = new Dictionary<int, ModdedComboNodeInfo>();
        foreach (var m in moddedNodes)
        {
            if (usedModded.Contains(m)) continue;
            byIndex.TryAdd(m.TreeIndex, m);
        }
        foreach (var kvp in vanillaMoves.OrderBy(k => k.Key))
        {
            if (groupToModded.ContainsKey(kvp.Key)) continue;
            if (!byIndex.TryGetValue(kvp.Key, out var m)) continue;
            groupToModded[kvp.Key] = m;
            usedModded.Add(m);
            byIndex.Remove(kvp.Key);
        }

        foreach (var kvp in vanillaMoves.OrderBy(k => k.Key))
        {
            var vanillaList = kvp.Value;
            if (!groupToModded.TryGetValue(kvp.Key, out var modded))
            {
                unmatched += vanillaList.Count;
                continue;
            }

            var paths = modded.AttackDbPaths
                .Select(NormalizeAnimPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var directed = new List<(string Path, string Dir, string Base, string BaseNoDir, bool Used)>();
            var shared = new List<(string Path, string Dir, string Base, string BaseNoDir, bool Used)>();
            foreach (var p in paths)
            {
                var dir = ExtractDirectionLabel(p);
                var bas = Path.GetFileNameWithoutExtension(p);
                var item = (Path: p, Dir: dir, Base: bas, BaseNoDir: StripDirectionSuffix(bas), Used: false);
                if (string.IsNullOrEmpty(dir))
                    shared.Add(item);
                else
                    directed.Add(item);
            }

            foreach (var vNode in vanillaList)
            {
                var vanillaDb = NormalizeAnimPath(
                    string.IsNullOrEmpty(vNode.DefaultDBPath) ? vNode.SourceDBPath : vNode.DefaultDBPath);
                var vDir = !string.IsNullOrEmpty(vNode.DirectionLabel)
                    ? vNode.DirectionLabel
                    : ExtractDirectionLabel(vanillaDb);
                var vBase = Path.GetFileNameWithoutExtension(vanillaDb);
                var vBaseNoDir = StripDirectionSuffix(vBase);

                int pickDir = -1, pickShared = -1;
                int bestDirScore = -1, bestSharedScore = -1;

                for (int i = 0; i < directed.Count; i++)
                {
                    if (directed[i].Used) continue;
                    int score = 0;
                    if (string.Equals(directed[i].Path, vanillaDb, StringComparison.OrdinalIgnoreCase))
                        score = 1000;
                    else
                    {
                        bool bothHaveDir = !string.IsNullOrEmpty(vDir) && !string.IsNullOrEmpty(directed[i].Dir);
                        if (bothHaveDir &&
                            !string.Equals(directed[i].Dir, vDir, StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (!string.IsNullOrEmpty(vDir) &&
                            string.Equals(directed[i].Dir, vDir, StringComparison.OrdinalIgnoreCase))
                            score += 100;
                        if (!string.IsNullOrEmpty(vBaseNoDir) &&
                            string.Equals(directed[i].BaseNoDir, vBaseNoDir, StringComparison.OrdinalIgnoreCase))
                            score += 80;
                        else if (!string.IsNullOrEmpty(vBase) &&
                                 string.Equals(directed[i].Base, vBase, StringComparison.OrdinalIgnoreCase))
                            score += 90;
                    }
                    if (score > bestDirScore)
                    {
                        bestDirScore = score;
                        pickDir = i;
                    }
                }

                for (int i = 0; i < shared.Count; i++)
                {
                    int score = 10;
                    if (string.Equals(shared[i].Path, vanillaDb, StringComparison.OrdinalIgnoreCase))
                        score = 1000;
                    else if (!string.IsNullOrEmpty(vBaseNoDir) &&
                             string.Equals(shared[i].BaseNoDir, vBaseNoDir, StringComparison.OrdinalIgnoreCase))
                        score = 80;
                    else if (!string.IsNullOrEmpty(vBase) &&
                             string.Equals(shared[i].Base, vBase, StringComparison.OrdinalIgnoreCase))
                        score = 90;
                    if (score > bestSharedScore)
                    {
                        bestSharedScore = score;
                        pickShared = i;
                    }
                }

                string? moddedDb = null;
                bool consumeDirected = false;
                bool sameAsVanilla = false;

                bool dirWins = pickDir >= 0 && bestDirScore >= 80 &&
                               (pickShared < 0 || bestDirScore >= bestSharedScore);
                bool sharedWins = pickShared >= 0 && bestSharedScore >= 80 &&
                                  (pickDir < 0 || bestSharedScore > bestDirScore);

                if (dirWins)
                {
                    moddedDb = directed[pickDir].Path;
                    consumeDirected = true;
                    sameAsVanilla = bestDirScore >= 1000;
                }
                else if (sharedWins)
                {
                    moddedDb = shared[pickShared].Path;
                    sameAsVanilla = bestSharedScore >= 1000;
                }
                else if (pickDir >= 0 && bestDirScore > 0)
                {
                    moddedDb = directed[pickDir].Path;
                    consumeDirected = true;
                    sameAsVanilla = bestDirScore >= 1000;
                }
                else if (pickShared >= 0)
                {
                    moddedDb = shared[pickShared].Path;
                    sameAsVanilla = bestSharedScore >= 1000;
                }
                else if (pickDir >= 0)
                {
                    moddedDb = directed[pickDir].Path;
                    consumeDirected = true;
                }

                if (moddedDb == null)
                {
                    unmatched++;
                    continue;
                }

                if (consumeDirected && pickDir >= 0)
                {
                    var used = directed[pickDir];
                    directed[pickDir] = used with { Used = true };
                }

                if (sameAsVanilla)
                    continue;

                var resolved = ResolveAnimationFromDB(moddedDb);
                if (string.IsNullOrEmpty(resolved))
                {
                    LogDebug($"[IMPORT] Could not resolve modded DB {moddedDb} for '{vNode.Name}' tree[{kvp.Key}] — keeping vanilla anim");
                    unmatched++;
                    continue;
                }

                vNode.AnimPath = resolved;
                vNode.SourceDBPath = moddedDb;
                var pathDir = ExtractDirectionLabel(moddedDb);
                var newDisplay = PrettifyAttackName(moddedDb, string.IsNullOrEmpty(pathDir) ? vDir : pathDir);
                if (!string.IsNullOrEmpty(newDisplay))
                    vNode.ImportedDisplayName = newDisplay;
                vNode.IsImportedFromMod = true;
                moveChanges++;
            }

            // AI payload diff (m_AIAttackAction class / m_Event) - applies regardless of
            // whether the anim pass matched; display + surfaced-node export path.
            ApplyAiPayloadDiff(vanillaList, modded, result);
        }

        if (!isMainChar)
        {
            var moddedByTree = moddedNodes
                .GroupBy(m => m.TreeIndex)
                .ToDictionary(g => g.Key, g => g.First());

            var vanillaRedirects = vanillaGraph.Nodes
                .Where(n => n.IsRedirect && n.TreeIndex >= 0)
                .ToList();

            foreach (var vRedir in vanillaRedirects)
            {
                if (!moddedByTree.TryGetValue(vRedir.TreeIndex, out var modded)) continue;
                if (modded.RedirectTargetTreeIndex < 0) continue;
                if (modded.RedirectTargetTreeIndex == vRedir.RedirectTargetTreeIndex) continue;

                var targetNode = vanillaGraph.Nodes.FirstOrDefault(n =>
                    n.TreeIndex == modded.RedirectTargetTreeIndex && !n.IsRedirect);
                if (targetNode == null)
                {
                    LogDebug($"[IMPORT] Redirect target tree[{modded.RedirectTargetTreeIndex}] not found for '{vRedir.Name}'");
                    unmatched++;
                    continue;
                }

                vRedir.ResolvedRedirectNodeId = targetNode.Id;
                // A' retarget: rows re-sourced onto the OLD play target belong to this
                // redirect - move them to the new play target so display and export
                // owner-writeback both follow the retarget.
                var ownedRows = vanillaGraph.Edges
                    .Where(e => e.OwnerRedirectNodeId == vRedir.Id && e.FromNodeId != targetNode.Id)
                    .ToList();
                vanillaGraph.Edges.RemoveAll(e => e.OwnerRedirectNodeId == vRedir.Id);
                vanillaGraph.Edges.RemoveAll(e => e.FromNodeId == vRedir.Id);
                vanillaGraph.Edges.Add(new ComboEdge
                {
                    FromNodeId = vRedir.Id,
                    ToNodeId = targetNode.Id,
                    InputName = "",
                    IsRedirect = true
                });
                foreach (var owned in ownedRows)
                    vanillaGraph.Edges.Add(new ComboEdge
                    {
                        FromNodeId = targetNode.Id,
                        ToNodeId = owned.ToNodeId,
                        InputName = owned.InputName,
                        OwnerRedirectNodeId = vRedir.Id
                    });
                retargetChanges++;
            }
        }

        int surfaced = SurfaceModdedOnlyNodes(vanillaGraph, moddedNodes, usedModded, groupToModded, isMainChar);

        result.MoveChanges = moveChanges;
        result.RetargetChanges = retargetChanges;
        result.Unmatched = unmatched;
        result.Surfaced = surfaced;

        var modToVanilla = BuildModToVanillaMap(vanillaGraph, groupToModded, moddedNodes);
        ApplyModdedRowOverrides(vanillaGraph, moddedNodes, groupToModded, modToVanilla, result);
        if (!isMainChar && moddedStartEntries is { Count: > 0 })
            ApplyModdedStartEntries(vanillaGraph, moddedStartEntries, modToVanilla, result);

        LogDebug($"[IMPORT] ApplyModdedComboToVanilla: {moveChanges} moves, {retargetChanges} retargets, {unmatched} unmatched, {surfaced} surfaced, {result.AiChanges} AI payload(s), {result.ChanceOverrides} chance override(s), {result.CondClears + result.CondInstances} cond rewire(s), {result.CondParamWrites} cond param(s), {result.StartTransitionsChanged} start row(s) ({weaponName})");
        return result;
    }

    /// <summary>
    /// Overwrites the matched vanilla node's AI payload fields when the modded node
    /// carries a different action class / event name. A mod payload REMOVAL keeps the
    /// vanilla payload (removing AI silently would break the tree) and is counted.
    /// </summary>
    private void ApplyAiPayloadDiff(List<ComboNode> vanillaList, ModdedComboNodeInfo modded, ModdedApplyResult result)
    {
        if (string.IsNullOrEmpty(modded.AiKind))
        {
            foreach (var vNode in vanillaList.Where(n => n.IsActionNode || n.IsEventNode))
            {
                result.AiRemovalsSkipped++;
                LogDebug($"[IMPORT] AI payload removed on '{vNode.Name}' tree[{vNode.TreeIndex}] - keeping vanilla ({(vNode.IsActionNode ? vNode.ActionClass : vNode.EventName)})");
            }
            return;
        }

        string wantToken = NormalizeAiToken(modded.AiClass);
        if (wantToken.Length == 0) return;

        foreach (var vNode in vanillaList)
        {
            if (modded.AiKind == "Action")
            {
                if (vNode.IsActionNode &&
                    string.Equals(NormalizeAiToken(vNode.ActionClass), wantToken, StringComparison.OrdinalIgnoreCase))
                    continue;
                vNode.IsActionNode = true;
                vNode.IsEventNode = false;
                vNode.EventName = "";
                vNode.ActionClass = modded.AiClass;
                vNode.IsImportedFromMod = true;
                result.AiChanges++;
            }
            else
            {
                if (vNode.IsEventNode &&
                    string.Equals(NormalizeAiToken(vNode.EventName), wantToken, StringComparison.OrdinalIgnoreCase))
                    continue;
                vNode.IsEventNode = true;
                vNode.IsActionNode = false;
                vNode.ActionClass = "";
                vNode.EventName = modded.AiClass;
                vNode.IsImportedFromMod = true;
                result.AiChanges++;
            }
        }
    }

    /// <summary>
    /// Class/object-name comparison token: strips _Copy_ suffixes, trailing _&lt;digits&gt;
    /// instance suffixes (BP_X_C_0), and a final _C - so a mod class import
    /// ("BP_X_C"), its generated class, and the vanilla parser's already-stripped name
    /// all normalize to the same token.
    /// </summary>
    private static string NormalizeAiToken(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var s = name!.Trim();
        int cut = s.IndexOf("_Copy_", StringComparison.Ordinal);
        if (cut > 0) s = s[..cut];
        for (int pass = 0; pass < 2; pass++)
        {
            int underscore = s.LastIndexOf('_');
            if (underscore > 0 && underscore + 1 < s.Length &&
                int.TryParse(s[(underscore + 1)..], out _))
                s = s[..underscore];
        }
        if (s.EndsWith("_C", StringComparison.Ordinal))
            s = s[..^2];
        return s;
    }

    /// <summary>
    /// modded tree index -> vanilla tree index for target-space translation. Attack
    /// nodes come from the name/index match; unmatched indices map identity only when
    /// the vanilla side of that index was not claimed by a different modded node.
    /// </summary>
    private static Dictionary<int, int> BuildModToVanillaMap(
        ComboGraph graph, Dictionary<int, ModdedComboNodeInfo> groupToModded,
        List<ModdedComboNodeInfo> moddedNodes)
    {
        var map = new Dictionary<int, int>();
        var claimedVanilla = groupToModded.Keys.ToHashSet();
        foreach (var (vTree, m) in groupToModded)
            map[m.TreeIndex] = vTree;
        foreach (var m in moddedNodes)
        {
            if (map.ContainsKey(m.TreeIndex)) continue;
            if (claimedVanilla.Contains(m.TreeIndex)) continue;
            if (graph.Nodes.Any(n => n.TreeIndex == m.TreeIndex))
                map[m.TreeIndex] = m.TreeIndex;
        }
        return map;
    }

    /// <summary>
    /// Diffs modded transition rows against the vanilla TreeRows snapshot and writes
    /// the delta into the graph's override maps. Rows are aligned by index with a
    /// signature guard (InputName + key set + verifiable targets through the
    /// mod->vanilla tree map); a mismatch means the mod restructured the rows and that
    /// node's chance/condition diff is skipped with a log. Condition re-homing: a
    /// vanilla row's condition is overwritten only by param edits on the same class; a
    /// mod-added condition reuses a vanilla same-class export (SetInstance); no match
    /// -> skipped + logged (raw mod export indices are never copied).
    /// </summary>
    private void ApplyModdedRowOverrides(
        ComboGraph graph, List<ModdedComboNodeInfo> moddedNodes,
        Dictionary<int, ModdedComboNodeInfo> groupToModded,
        Dictionary<int, int> modToVanilla, ModdedApplyResult result)
    {
        if (graph.TreeRows.Count == 0) return;

        // Candidate same-class vanilla condition exports + their baseline params,
        // for SetInstance re-homing of mod-added conditions.
        var vanillaCondByClass = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var vanillaCondParamsByExport = new Dictionary<int, List<ConditionParamInfo>>();
        foreach (var (_, rows) in graph.TreeRows)
        {
            foreach (var r in rows)
            {
                if (r.ConditionInstanceIndex <= 0) continue;
                var tok = NormalizeAiToken(r.ConditionClass);
                if (tok.Length > 0 && !vanillaCondByClass.ContainsKey(tok))
                    vanillaCondByClass[tok] = r.ConditionInstanceIndex;
                if (r.ConditionParamList is { Count: > 0 } &&
                    !vanillaCondParamsByExport.ContainsKey(r.ConditionInstanceIndex))
                    vanillaCondParamsByExport[r.ConditionInstanceIndex] = r.ConditionParamList;
            }
        }

        bool SignatureMatches(TreeRowInfo vRow, ModdedTransitionRowInfo mRow)
        {
            if (!string.Equals(vRow.InputName, mRow.InputName, StringComparison.OrdinalIgnoreCase))
                return false;
            if (vRow.Keys.Count != mRow.Keys.Count) return false;
            for (int i = 0; i < mRow.Keys.Count; i++)
                if (vRow.Keys[i] != mRow.Keys[i]) return false;
            for (int i = 0; i < mRow.Keys.Count && i < mRow.TargetTreeIndices.Count; i++)
            {
                int mt = mRow.TargetTreeIndices[i];
                if (mt < 0) continue;
                if (!modToVanilla.TryGetValue(mt, out int vt)) continue;
                if (i < vRow.TargetTreeIndices.Count && vRow.TargetTreeIndices[i] >= 0 &&
                    vRow.TargetTreeIndices[i] != vt)
                    return false;
            }
            return true;
        }

        void DiffNode(int vTree, ModdedComboNodeInfo m)
        {
            if (!graph.TreeRows.TryGetValue(vTree, out var vRows)) return;
            if (m.TransitionRows.Count == 0 && vRows.Count == 0) return;

            // Row-count mismatch = restructured rows (overhaul mods) - skip the node.
            if (m.TransitionRows.Count != vRows.Count)
            {
                if (m.TransitionRows.Count > 0 || vRows.Count > 0)
                {
                    result.CondSkipped++;
                    LogDebug($"[IMPORT] ROWS SKIPPED (restructured) tree[{vTree}]: {vRows.Count} vanilla vs {m.TransitionRows.Count} modded row(s)");
                }
                return;
            }

            for (int rowIdx = 0; rowIdx < vRows.Count; rowIdx++)
            {
                var vRow = vRows[rowIdx];
                var mRow = m.TransitionRows[rowIdx];
                if (!SignatureMatches(vRow, mRow))
                {
                    result.CondSkipped++;
                    LogDebug($"[IMPORT] ROWS SKIPPED (restructured) tree[{vTree}] row {rowIdx}: signature mismatch");
                    return;
                }

                if (Math.Abs(mRow.Probability - vRow.OriginalProbability) > 0.001f)
                {
                    if (!graph.TreeRowProbabilityOverrides.TryGetValue(vTree, out var pMap))
                        graph.TreeRowProbabilityOverrides[vTree] = pMap = new Dictionary<int, float>();
                    pMap[rowIdx] = mRow.Probability;
                    result.ChanceOverrides++;
                }

                bool vHasCond = vRow.ConditionInstanceIndex > 0;
                bool mHasCond = !string.IsNullOrEmpty(mRow.ConditionClass);
                if (vHasCond && !mHasCond)
                {
                    if (!graph.TreeRowConditionOverrides.TryGetValue(vTree, out var cMap))
                        graph.TreeRowConditionOverrides[vTree] = cMap = new Dictionary<int, TreeRowConditionEdit>();
                    cMap[rowIdx] = new TreeRowConditionEdit { ClearCondition = true };
                    result.CondClears++;
                }
                else if (vHasCond && mHasCond)
                {
                    if (!string.Equals(NormalizeAiToken(vRow.ConditionClass), NormalizeAiToken(mRow.ConditionClass),
                            StringComparison.OrdinalIgnoreCase))
                    {
                        result.CondSkipped++;
                        LogDebug($"[IMPORT] COND SKIP (class change) tree[{vTree}] row {rowIdx}: {vRow.ConditionClass} -> {mRow.ConditionClass}");
                        continue;
                    }
                    result.CondParamWrites += ApplyConditionParamDiff(
                        graph, vRow.ConditionInstanceIndex, vRow.ConditionParamList, mRow.ConditionParams);
                }
                else if (!vHasCond && mHasCond)
                {
                    var tok = NormalizeAiToken(mRow.ConditionClass);
                    if (tok.Length > 0 && vanillaCondByClass.TryGetValue(tok, out int shareIdx))
                    {
                        if (!graph.TreeRowConditionOverrides.TryGetValue(vTree, out var cMap))
                            graph.TreeRowConditionOverrides[vTree] = cMap = new Dictionary<int, TreeRowConditionEdit>();
                        cMap[rowIdx] = new TreeRowConditionEdit
                        {
                            SetInstance = shareIdx,
                            CloneClass = mRow.ConditionClass
                        };
                        result.CondInstances++;
                        vanillaCondParamsByExport.TryGetValue(shareIdx, out var baseline);
                        result.CondParamWrites += ApplyConditionParamDiff(
                            graph, shareIdx, baseline, mRow.ConditionParams);
                    }
                    else
                    {
                        result.CondSkipped++;
                        LogDebug($"[IMPORT] COND SKIP tree[{vTree}] row {rowIdx}: no vanilla {mRow.ConditionClass} export to share");
                    }
                }
            }
        }

        foreach (var (vTree, m) in groupToModded)
            DiffNode(vTree, m);

        // Redirect structs keep their own rows (A' pass-through sources) - pair by
        // tree index and run the same guarded diff.
        var matchedModTrees = groupToModded.Values.Select(m => m.TreeIndex).ToHashSet();
        var vanillaRedirectTrees = graph.Nodes
            .Where(n => n.IsRedirect && n.TreeIndex >= 0)
            .Select(n => n.TreeIndex)
            .Distinct();
        foreach (var vTree in vanillaRedirectTrees)
        {
            if (matchedModTrees.Contains(vTree)) continue;
            var m = moddedNodes.FirstOrDefault(x => x.TreeIndex == vTree && x.RedirectTargetTreeIndex >= 0);
            if (m == null) continue;
            DiffNode(vTree, m);
        }
    }

    /// <summary>Writes differing modded condition params (same dotted paths) into the
    /// export-keyed override map. Returns the number of param writes.</summary>
    private static int ApplyConditionParamDiff(
        ComboGraph graph, int exportIdx1Based,
        List<ConditionParamInfo>? baseline, List<ConditionParamInfo> moddedParams)
    {
        if (exportIdx1Based <= 0 || moddedParams.Count == 0) return 0;
        var baseMap = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in baseline ?? Enumerable.Empty<ConditionParamInfo>())
            if (!string.IsNullOrEmpty(p.Path))
                baseMap[p.Path] = p.Value;

        Dictionary<string, string>? writes = null;
        foreach (var p in moddedParams)
        {
            if (string.IsNullOrEmpty(p.Path)) continue;
            if (baseMap.TryGetValue(p.Path, out var bv) &&
                string.Equals(bv, p.Value, StringComparison.Ordinal))
                continue;
            (writes ??= new Dictionary<string, string>(StringComparer.Ordinal))[p.Path] = p.Value;
        }
        if (writes == null || writes.Count == 0) return 0;

        if (!graph.TreeConditionParamOverrides.TryGetValue(exportIdx1Based, out var map))
            graph.TreeConditionParamOverrides[exportIdx1Based] = map = new Dictionary<string, string>();
        foreach (var kv in writes)
            map[kv.Key] = kv.Value;
        return writes.Count;
    }

    /// <summary>
    /// Overwrites current (non-Original*) fields on vanilla-matched start entries:
    /// probabilities and per-key targets remapped through the mod->vanilla tree map.
    /// Original* baselines stay untouched so the change list and export diff keep
    /// working. Entries matched by ticket, then label; unmatched mod entries are
    /// structural adds (log-only MVP) and unmatched vanilla entries are left alone.
    /// </summary>
    private void ApplyModdedStartEntries(
        ComboGraph graph, List<ModdedStartEntryInfo> moddedEntries,
        Dictionary<int, int> modToVanilla, ModdedApplyResult result)
    {
        var vanillaEntries = graph.StartEntries ?? new List<StartEntry>();
        if (vanillaEntries.Count == 0)
        {
            result.StartStructuralSkipped += moddedEntries.Count;
            foreach (var m in moddedEntries)
                LogDebug($"[IMPORT] START ADD (skipped, no vanilla entries): '{m.Label}' [{m.TicketEnumValue}]");
            return;
        }

        var used = new HashSet<StartEntry>();
        var byTicket = new Dictionary<string, StartEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in vanillaEntries)
            if (!string.IsNullOrEmpty(v.TicketEnumValue) && !byTicket.ContainsKey(v.TicketEnumValue))
                byTicket[v.TicketEnumValue] = v;
        var byLabel = new Dictionary<string, StartEntry>(StringComparer.Ordinal);
        foreach (var v in vanillaEntries)
            if (!string.IsNullOrEmpty(v.Label) && !byLabel.ContainsKey(v.Label))
                byLabel[v.Label] = v;

        foreach (var m in moddedEntries)
        {
            StartEntry? v = null;
            if (!string.IsNullOrEmpty(m.TicketEnumValue) && byTicket.TryGetValue(m.TicketEnumValue, out var byT) &&
                used.Add(byT))
                v = byT;
            else if (!string.IsNullOrEmpty(m.Label) && byLabel.TryGetValue(m.Label, out var byL) &&
                     used.Add(byL))
                v = byL;
            else
            {
                result.StartStructuralSkipped++;
                LogDebug($"[IMPORT] START ADD (skipped): '{m.Label}' [{m.TicketEnumValue}] - no vanilla ticket/label match");
                continue;
            }

            if (v.Removed) continue;

            if (!string.Equals(NormalizeAiToken(v.ConditionClass), NormalizeAiToken(m.ConditionClass),
                    StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(m.ConditionClass))
            {
                LogDebug($"[IMPORT] START COND SKIP: '{v.Label}' condition class change not imported ({v.ConditionClass} -> {m.ConditionClass})");
            }

            bool entryChanged = false;
            int limit = Math.Min(v.Transitions.Count, m.Transitions.Count);
            if (m.Transitions.Count != v.Transitions.Count)
                LogDebug($"[IMPORT] START ROWS: '{v.Label}' {v.Transitions.Count} vanilla vs {m.Transitions.Count} modded transition(s) - diffing first {limit}");

            for (int i = 0; i < limit; i++)
            {
                var st = v.Transitions[i];
                var mt = m.Transitions[i];
                if (st.Removed) continue;
                bool changed = false;

                if (Math.Abs(mt.Probability - st.OriginalProbability) > 0.001f &&
                    Math.Abs(mt.Probability - st.Probability) > 0.001f)
                {
                    st.Probability = mt.Probability;
                    changed = true;
                }

                for (int k = 0; k < mt.Keys.Count && k < st.Keys.Count; k++)
                {
                    if (mt.Keys[k] != st.Keys[k]) continue;
                    int mtIdx = k < mt.KeyTargetTreeIndices.Count ? mt.KeyTargetTreeIndices[k] : -1;
                    if (mtIdx < 0) continue;
                    if (!modToVanilla.TryGetValue(mtIdx, out int vt)) continue;
                    if (k < st.KeyTargetTreeIndices.Count && st.KeyTargetTreeIndices[k] == vt) continue;

                    if (k < st.KeyTargetTreeIndices.Count) st.KeyTargetTreeIndices[k] = vt;
                    var node = graph.Nodes.FirstOrDefault(n => n.TreeIndex == vt && !n.IsRedirect);
                    if (k < st.KeyTargetNodeIds.Count) st.KeyTargetNodeIds[k] = node?.Id ?? -1;
                    if (k < st.KeyTargetNames.Count) st.KeyTargetNames[k] = node?.Name ?? "";
                    changed = true;
                }

                if (changed)
                {
                    // Primary target mirrors the first key with a resolvable tree index
                    // (same rule the vanilla parse uses).
                    for (int k = 0; k < st.KeyTargetTreeIndices.Count; k++)
                    {
                        if (st.KeyTargetTreeIndices[k] < 0) continue;
                        st.TargetTreeIndex = st.KeyTargetTreeIndices[k];
                        st.TargetNodeId = k < st.KeyTargetNodeIds.Count ? st.KeyTargetNodeIds[k] : -1;
                        st.TargetName = k < st.KeyTargetNames.Count ? st.KeyTargetNames[k] : "";
                        break;
                    }
                    result.StartTransitionsChanged++;
                    entryChanged = true;
                }
            }

            if (entryChanged)
                result.StartEntriesChanged++;
        }
    }

    /// <summary>
    /// Adds modded tree entries that have no vanilla counterpart (WW-style appended nodes) to the
    /// editor graph as Kind=ImportedNew nodes, with their transitions as edges. Enemy trees only
    /// for now - MainChar surfacing is deferred with the rest of the MainChar scope.
    /// </summary>
    private int SurfaceModdedOnlyNodes(
        ComboGraph graph,
        List<ModdedComboNodeInfo> moddedNodes,
        HashSet<ModdedComboNodeInfo> usedModded,
        Dictionary<int, ModdedComboNodeInfo> groupToModded,
        bool isMainChar)
    {
        var extras = moddedNodes.Where(m => !usedModded.Contains(m)).ToList();
        if (extras.Count == 0) return 0;
        if (isMainChar)
        {
            LogDebug($"[IMPORT] {extras.Count} modded-only node(s) on MainChar tree - surfacing deferred (enemy trees first)");
            return 0;
        }

        int nextId = graph.Nodes.Count > 0 ? graph.Nodes.Max(n => n.Id) + 1 : 0;
        var extraIdsByTreeIndex = new Dictionary<int, List<int>>();
        var extraRedirects = new List<(ComboNode node, int targetTreeIndex)>();
        int surfacedNodes = 0;

        foreach (var m in extras)
        {
            string nodeName = m.Name == "None" ? "" : m.Name;
            string baseDisplayName = string.IsNullOrEmpty(nodeName)
                ? $"Node_{m.TreeIndex}"
                : nodeName
                    .Replace("MainChar_", "")
                    .Replace("_", " ")
                    .Replace("Attack barehands ", "")
                    .Replace("attack barehands ", "");
            bool isRoot = nodeName.Contains("Conduit", StringComparison.OrdinalIgnoreCase) ||
                          nodeName.Contains("Root", StringComparison.OrdinalIgnoreCase);
            bool isRedirect = m.RedirectTargetTreeIndex >= 0;

            var attacks = m.AttackDbPaths
                .Select(NormalizeAnimPath)
                .Where(p => !string.IsNullOrEmpty(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(db => (db, anim: ResolveAnimationFromDB(db) ?? ""))
                .ToList();

            var createdIds = new List<int>();
            if (attacks.Count == 0 || isRedirect)
            {
                var node = new ComboNode
                {
                    Id = nextId++,
                    TreeIndex = m.TreeIndex,
                    Name = nodeName,
                    AnimPath = "",
                    DefaultAnimPath = "",
                    DefaultDBPath = "",
                    SourceDBPath = "",
                    DisplayName = baseDisplayName,
                    IsRoot = isRoot,
                    DirectionLabel = "",
                    IsRedirect = isRedirect,
                    RedirectTargetTreeIndex = m.RedirectTargetTreeIndex,
                    Kind = ComboNodeKind.ImportedNew
                };
                // AI-payload nodes (m_AIAttackAction / m_Event) export through the
                // clone-from-import-source flow: point ImportComboRel at the modded
                // combo file (harvested into CustomAssets) and keep the tree index.
                if (!isRedirect && m.AiKind is "Action" or "Event")
                {
                    node.IsActionNode = m.AiKind == "Action";
                    node.ActionClass = m.AiKind == "Action" ? m.AiClass : "";
                    node.IsEventNode = m.AiKind == "Event";
                    node.EventName = m.AiKind == "Event" ? m.AiClass : "";
                    node.ImportComboRel = m.ModdedContentRel;
                    node.ImportSourceTreeIndex = m.TreeIndex;
                    node.ImportedDisplayName = m.AiClass;
                }
                graph.Nodes.Add(node);
                createdIds.Add(node.Id);
                surfacedNodes++;
                if (isRedirect) extraRedirects.Add((node, m.RedirectTargetTreeIndex));
            }
            else
            {
                var distinctDirs = attacks
                    .Select(a => ExtractDirectionLabel(a.db))
                    .Where(d => !string.IsNullOrEmpty(d))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                bool showDirLabels = distinctDirs.Count > 1;
                foreach (var (db, anim) in attacks)
                {
                    var dir = ExtractDirectionLabel(db);
                    var displayName = baseDisplayName;
                    if (showDirLabels && !string.IsNullOrEmpty(dir) &&
                        !displayName.EndsWith(dir, StringComparison.OrdinalIgnoreCase))
                        displayName = $"{baseDisplayName} {dir}";

                    var node = new ComboNode
                    {
                        Id = nextId++,
                        TreeIndex = m.TreeIndex,
                        Name = nodeName,
                        AnimPath = anim,
                        DefaultAnimPath = anim,
                        DefaultDBPath = db,
                        SourceDBPath = db,
                        DisplayName = displayName,
                        ImportedDisplayName = PrettifyAttackName(db, dir),
                        IsRoot = isRoot,
                        DirectionLabel = dir,
                        Kind = ComboNodeKind.ImportedNew
                    };
                    graph.Nodes.Add(node);
                    createdIds.Add(node.Id);
                    surfacedNodes++;
                }
            }
            extraIdsByTreeIndex[m.TreeIndex] = createdIds;
        }

        // Resolve redirect targets (extra -> extra, extra -> vanilla).
        foreach (var (node, targetTreeIndex) in extraRedirects)
        {
            int resolved = -1;
            if (extraIdsByTreeIndex.TryGetValue(targetTreeIndex, out var ids) && ids.Count > 0)
                resolved = ids[0];
            else
                resolved = graph.Nodes.FirstOrDefault(n => n.TreeIndex == targetTreeIndex)?.Id ?? -1;
            node.ResolvedRedirectNodeId = resolved;
            if (resolved >= 0)
                graph.RedirectOriginalTargets[node.Id] = resolved;
        }

        List<int> IdsFor(int treeIndex)
        {
            if (extraIdsByTreeIndex.TryGetValue(treeIndex, out var ids)) return ids;
            return graph.Nodes.Where(n => n.TreeIndex == treeIndex).Select(n => n.Id).ToList();
        }

        var candidateEdges = new List<ComboEdge>();
        // ownerRedirectTreeIndex >= 0: fromTreeIndex is a modded redirect whose own rows
        // continue from the attack node it plays (A' pass-through) - same rule as the
        // vanilla parse. The edge is tagged with the redirect's graph id.
        void AddEdgesFrom(int fromTreeIndex, List<ModdedTransitionInfo> transitions, int ownerRedirectTreeIndex)
        {
            if (transitions.Count == 0) return;
            List<int> fromIds;
            int ownerId = -1;
            if (ownerRedirectTreeIndex >= 0)
            {
                var rId = IdsFor(fromTreeIndex).FirstOrDefault(-1);
                var playIds = IdsFor(ownerRedirectTreeIndex);
                if (rId < 0 || playIds.Count == 0) return;
                if (playIds.All(id => graph.Nodes.FirstOrDefault(n => n.Id == id)?.IsRedirect == true)) return;
                fromIds = playIds;
                ownerId = rId;
            }
            else
            {
                fromIds = IdsFor(fromTreeIndex);
            }
            if (fromIds.Count == 0) return;
            foreach (var t in transitions)
            {
                if (t.TargetTreeIndex < 0) continue;
                var toIds = IdsFor(t.TargetTreeIndex);
                foreach (var f in fromIds)
                    foreach (var to in toIds)
                        candidateEdges.Add(new ComboEdge
                        {
                            FromNodeId = f,
                            ToNodeId = to,
                            InputName = t.InputName,
                            OwnerRedirectNodeId = ownerId
                        });
            }
        }

        foreach (var m in extras)
            AddEdgesFrom(m.TreeIndex, m.Transitions, m.RedirectTargetTreeIndex >= 0 ? m.RedirectTargetTreeIndex : -1);

        // Vanilla nodes whose modded counterpart now transitions into a surfaced node
        // (the mod re-wires an existing node into its new content - WW's core technique).
        foreach (var (vanillaTreeIndex, m) in groupToModded)
        {
            var intoExtras = m.Transitions
                .Where(t => extraIdsByTreeIndex.ContainsKey(t.TargetTreeIndex))
                .ToList();
            if (intoExtras.Count > 0)
                AddEdgesFrom(vanillaTreeIndex, intoExtras, -1);
        }

        var existingPairs = graph.Edges.Select(e => (e.FromNodeId, e.ToNodeId)).ToHashSet();
        int addedEdges = 0;
        foreach (var e in candidateEdges)
        {
            if (e.FromNodeId == e.ToNodeId) continue;
            if (!existingPairs.Add((e.FromNodeId, e.ToNodeId))) continue;
            graph.Edges.Add(e);
            if (e.OwnerRedirectNodeId >= 0)
            {
                var owner = graph.Nodes.FirstOrDefault(n => n.Id == e.OwnerRedirectNodeId);
                if (owner != null) owner.OwnedContinuation = true;
            }
            addedEdges++;
        }

        LogDebug($"[IMPORT] Surfaced {surfacedNodes} modded-only node(s) ({extras.Count} m_Nodes entries), {addedEdges} edge(s)");
        return surfacedNodes;
    }

    private string ExtractInputName(FStructFallback transitionData)
    {
        var inputProp = transitionData.Properties.FirstOrDefault(p => p.Name.Text == "m_eInputTransition");
        if (inputProp?.Tag is EnumProperty inputEnum)
            return InputNameFromEnumRaw(inputEnum.Value.Text);
        return "";
    }

    private static string ResolveConditionClass(FStructFallback rowData)
    {
        if (rowData.Properties.FirstOrDefault(p => p.Name.Text == "m_ConditionInstance")?.Tag is not ObjectProperty cond
            || cond.Value == null)
            return "";
        var resolved = cond.Value.ResolvedObject;
        if (resolved == null) return "";
        return resolved.Class?.Name.Text ?? resolved.Name.Text ?? "";
    }

    private static int ResolveConditionInstanceIndex(FStructFallback rowData)
    {
        if (rowData.Properties.FirstOrDefault(p => p.Name.Text == "m_ConditionInstance")?.Tag is not ObjectProperty cond
            || cond.Value == null)
            return 0;
        return cond.Value.Index;
    }

    private static List<ConditionParamInfo> ResolveConditionParams(FStructFallback rowData)
    {
        var result = new List<ConditionParamInfo>();
        if (rowData.Properties.FirstOrDefault(p => p.Name.Text == "m_ConditionInstance")?.Tag is not ObjectProperty cond
            || cond.Value == null)
            return result;
        var resolved = cond.Value.ResolvedObject;
        if (resolved?.Object == null)
            return result;
        UObject? conditionExport = null;
        try
        {
            conditionExport = resolved.Object.Value;
        }
        catch
        {
            return result;
        }
        if (conditionExport?.Properties == null)
            return result;
        CollectConditionParams(conditionExport, "", result, new HashSet<UObject>(), 0);
        return result;
    }

    private static void CollectConditionParams(UObject ex, string prefix, List<ConditionParamInfo> result,
        HashSet<UObject> seen, int depth)
    {
        if (depth > 3 || !seen.Add(ex) || ex.Properties == null) return;
        CollectFromTags(ex.Properties, prefix, result, seen, depth);
    }

    private static void CollectFromTags(List<FPropertyTag> props, string prefix, List<ConditionParamInfo> result,
        HashSet<UObject> seen, int depth)
    {
        foreach (var p in props)
        {
            string raw = p.Name.Text;
            if (raw is "m_ConditionID" or "m_PerConditionValueToNodeMap" or "m_iSerializeVersion")
                continue;
            if (p.Tag is ObjectProperty op && op.Value != null && op.Value.ResolvedObject is { Object: { } nestedLazy })
            {
                UObject? nested = null;
                try
                {
                    nested = nestedLazy.Value;
                }
                catch
                {
                    nested = null;
                }
                if (nested != null && nested.Properties is { Count: > 0 })
                {
                    CollectConditionParams(nested, prefix + raw + ".", result, seen, depth + 1);
                    continue;
                }
            }
            if (p.Tag is StructProperty sp && sp.Value?.StructType is FStructFallback sf)
            {
                CollectFromTags(sf.Properties, prefix + raw + ".", result, seen, depth + 1);
                continue;
            }
            string v = FormatConditionParam(p.Tag);
            if (v.Length > 0)
                result.Add(new ConditionParamInfo { Path = prefix + raw, Kind = ParamKindOf(p.Tag), Value = v });
        }
    }

    private static string ParamKindOf(FPropertyTagType? t)
    {
        return t switch
        {
            EnumProperty => "Enum",
            IntProperty or Int8Property or Int16Property or Int64Property => "Int",
            UInt16Property or UInt32Property or UInt64Property => "Int",
            FloatProperty or DoubleProperty => "Float",
            BoolProperty => "Bool",
            NameProperty => "Name",
            StrProperty => "Str",
            ByteProperty => "Byte",
            _ => "ReadOnly"
        };
    }

    private static string FormatConditionParam(FPropertyTagType? t)
    {
        switch (t)
        {
            case EnumProperty ep: return ep.Value.Text;
            case IntProperty ip: return ip.Value.ToString();
            case Int8Property i8: return i8.Value.ToString();
            case Int16Property i16: return i16.Value.ToString();
            case Int64Property i6: return i6.Value.ToString();
            case UInt16Property u16: return u16.Value.ToString();
            case UInt32Property u: return u.Value.ToString();
            case UInt64Property u64: return u64.Value.ToString();
            case FloatProperty f: return f.Value.ToString("0.###");
            case DoubleProperty d: return d.Value.ToString("0.###");
            case BoolProperty b: return b.Value.ToString();
            case NameProperty n: return n.Value.Text;
            case StrProperty s: return s.Value?.ToString() ?? "";
            case ByteProperty by: return by.Value.ToString();
            case ObjectProperty o:
                if (o.Value == null) return "null";
                return o.Value.ResolvedObject?.Class?.Name.Text
                    ?? o.Value.ResolvedObject?.Name.Text
                    ?? ("idx " + o.Value.Index);
            case ArrayProperty a: return (a.Value?.Properties?.Count ?? 0) + " entries";
            case MapProperty m: return ((m.Value?.Properties?.Count) ?? 0) + " pairs";
            case StructProperty st:
                if (st.Value?.StructType is FGameplayTagContainer gtc)
                    return string.Join(", ", gtc.GameplayTags.Select(gt => gt.TagName.Text));
                return "<" + (st.Value?.StructType?.GetType().Name ?? "struct") + ">";
            default: return "";
        }
    }

    internal static string InputNameFromEnumRaw(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        var name = raw.Contains("::") ? raw.Split("::")[^1] : raw;
        return name switch
        {
            "Light" => "LMB",
            "Heavy" => "RMB",
            "HeavyHold" => "RMB Hold",
            "HeavyAlt" => "RMB",
            "Special" => "S",
            "Dodge" => "Shift",
            "Throw" => "Q",
            "None" or "" => "",
            _ => name
        };
    }



    private static readonly Dictionary<string, string> AnimFallbacks = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Game/Animations/MainChar/Attacks/Man/Barehands/Skills/LightCombo/MultiHit/MainChar_Attack_Man_Barehands_Skill_LightCombo_MultiHit_FL"] =
            "Game/Animations/MainChar/Attacks/SpecialCombos/AltPakMei/MainChar_Attack_SpecialCombo_AltPakMei_MultiHitClaws",
        ["Game/Animations/MainChar/Attacks/Man/Barehands/Skills/LightCombo/MultiHit/MainChar_Attack_Man_Barehands_Skill_LightCombo_MultiHit_BR"] =
            "Game/Animations/MainChar/Attacks/Man/Barehands/LightCombo/MainChar_Attack_Man_Barehands_Pressure_TripleHit_BR"
    };

    private UAnimSequence? LoadAnimSequence(string gamePath)
    {
        var obj = _provider!.SafeLoadPackageObject<UAnimSequence>(gamePath);
        if (obj != null) return obj;

        try
        {
            if (_provider.TryLoadPackage(gamePath, out var package) && package != null)
            {
                foreach (var export in package.GetExports())
                {
                    if (export is UAnimSequence anim)
                    {
                        LogDebug($"[ANIM] Export-name fallback OK: {gamePath}");
                        return anim;
                    }
                }
            }
        }
        catch { }
        return null;
    }

    public bool ValidateAnimation(string gamePath)
    {
        if (_provider == null) return false;
        try
        {
            if (MoveInfo.IsAttackDbGamePath(gamePath))
            {
                var resolved = ResolveAnimationFromDB(gamePath);
                if (string.IsNullOrEmpty(resolved)) return false;
                var obj = LoadAnimSequence(resolved);
                if (obj == null) return false;
                var skeletonObj = obj.Skeleton?.Load<USkeleton>();
                return skeletonObj != null;
            }

            var animObj = LoadAnimSequence(gamePath);
            if (animObj == null && AnimFallbacks.TryGetValue(gamePath, out var fallback))
                animObj = LoadAnimSequence(fallback);
            if (animObj == null) return false;

            var skeleton = animObj.Skeleton?.Load<USkeleton>();
            return skeleton != null;
        }
        catch { return false; }
    }

    public AnimationJsonData? LoadAnimation(string gamePath)
    {
        if (_provider == null) return null;

        if (MoveInfo.IsAttackDbGamePath(gamePath))
        {
            var resolved = ResolveAnimationFromDB(gamePath);
            if (string.IsNullOrEmpty(resolved)) return null;
            gamePath = resolved;
        }

        try
        {
            LogDebug($"[ANIM] Loading: {gamePath}");
            var obj = LoadAnimSequence(gamePath);
            LogDebug($"[ANIM] Direct load: {(obj != null ? "OK" : "null")}");

            if (obj == null && AnimFallbacks.TryGetValue(gamePath, out var fallback))
                obj = LoadAnimSequence(fallback);

            if (obj == null) return null;

            var skeletonObj = obj.Skeleton?.Load<USkeleton>();
            if (skeletonObj == null)
            {
                if (gamePath.Contains("CrookedFoot", StringComparison.OrdinalIgnoreCase))
                {
                    var grabPath = "Game/Animations/MainChar/Attacks/Man/Barehands/PostParry/PostParry_Knockdown/MainChar_Attack_Man_Barehands_PunishKD_CrookedFoot_Grab_attacker";
                    var grabObj = _provider.SafeLoadPackageObject<UAnimSequence>(grabPath);
                    if (grabObj != null)
                    {
                        var grabSkeleton = grabObj.Skeleton?.Load<USkeleton>();
                        if (grabSkeleton != null)
                        {
                            var grabAnimSet = grabSkeleton.ConvertAnims(grabObj);
                            if (grabAnimSet.Sequences.Count > 0)
                                return BuildAnimationJson(grabAnimSet.Sequences[0], grabSkeleton);
                        }
                    }
                }
                LogDebug($"[ANIM] Skeleton load failed for {gamePath}");
                return null;
            }

            var animSet = skeletonObj.ConvertAnims(obj);
            if (animSet.Sequences.Count == 0) return null;

            return BuildAnimationJson(animSet.Sequences[0], skeletonObj);
        }
        catch (Exception ex)
        {
            var inner = ex.InnerException ?? ex;
            throw new Exception($"Failed to load {gamePath}: {inner.Message}", inner);
        }
    }

    private AnimationJsonData BuildAnimationJson(CAnimSequence seq, USkeleton skeleton)
    {
        var refSkeleton = skeleton.ReferenceSkeleton;
        var boneCount = refSkeleton.FinalRefBoneInfo.Length;

        var boneDataList = new BoneData[boneCount];
        var bindPoseList = new BindPoseTransform[boneCount];

        for (int i = 0; i < boneCount; i++)
        {
            var info = refSkeleton.FinalRefBoneInfo[i];
            var pose = refSkeleton.FinalRefBonePose[i];

            boneDataList[i] = new BoneData
            {
                name = info.Name.Text,
                parent = info.ParentIndex
            };

            bindPoseList[i] = new BindPoseTransform
            {
                position = new PosVec
                {
                    x = pose.Translation.X,
                    y = pose.Translation.Z,
                    z = pose.Translation.Y
                },
                rotation = new RotVec
                {
                    x = -pose.Rotation.X,
                    y = -pose.Rotation.Z,
                    z = -pose.Rotation.Y,
                    w = pose.Rotation.W
                }
            };
        }

        int numFrames = seq.NumFrames;
        float fps = seq.FramesPerSecond > 0 ? seq.FramesPerSecond : 30f;
        float duration = seq.AnimEndTime > 0 ? seq.AnimEndTime : (numFrames > 1 ? (numFrames - 1) / fps : 1f / fps);

        var tracksList = new List<AnimTrackData>();

        for (int boneIdx = 0; boneIdx < boneCount && boneIdx < seq.Tracks.Count; boneIdx++)
        {
            var track = seq.Tracks[boneIdx];
            if (track == null || !track.HasKeys()) continue;

            if (track.KeyQuat.Length == 0 && track.KeyPos.Length == 0) continue;

            var boneName = refSkeleton.FinalRefBoneInfo[boneIdx].Name.Text;
            var trackData = new AnimTrackData
            {
                boneName = boneName,
                boneIndex = boneIdx,
                times = new float[numFrames],
                positions = new PosVec[numFrames],
                rotations = new RotVec[numFrames],
                scales = new PosVec[numFrames]
            };

            var dstQuat = new CUE4Parse.UE4.Objects.Core.Math.FQuat(0, 0, 0, 1);
            var dstPos = new CUE4Parse.UE4.Objects.Core.Math.FVector(0, 0, 0);
            var dstScale = new CUE4Parse.UE4.Objects.Core.Math.FVector(1, 1, 1);

            for (int frame = 0; frame < numFrames; frame++)
            {
                dstQuat = new CUE4Parse.UE4.Objects.Core.Math.FQuat(0, 0, 0, 1);
                dstPos = new CUE4Parse.UE4.Objects.Core.Math.FVector(0, 0, 0);
                dstScale = new CUE4Parse.UE4.Objects.Core.Math.FVector(1, 1, 1);

                track.GetBoneTransform(frame, numFrames, ref dstQuat, ref dstPos, ref dstScale);

                trackData.times[frame] = (float)frame / fps;
                trackData.positions[frame] = new PosVec
                {
                    x = dstPos.X,
                    y = dstPos.Z,
                    z = dstPos.Y
                };
                trackData.rotations[frame] = new RotVec
                {
                    x = -dstQuat.X,
                    y = -dstQuat.Z,
                    z = -dstQuat.Y,
                    w = dstQuat.W
                };
                trackData.scales[frame] = new PosVec
                {
                    x = dstScale.X,
                    y = dstScale.Z,
                    z = dstScale.Y
                };
            }

            tracksList.Add(trackData);
        }

        var animData = new AnimationJson
        {
            name = seq.Name,
            duration = duration,
            numFrames = numFrames,
            fps = fps,
            tracks = tracksList.ToArray()
        };

        return new AnimationJsonData
        {
            skeleton = new SkeletonJson
            {
                bones = boneDataList,
                bindPose = bindPoseList
            },
            animation = animData
        };
    }

    public MeshJsonData? LoadMesh(string character)
    {
        if (_provider == null) return null;

        string? meshPath = null;
        if (CharacterMeshPaths.TryGetValue(character, out var mappedPath))
        {
            meshPath = mappedPath;
        }
        else
        {
            meshPath = FindMeshPath(character);
        }

        if (meshPath == null)
        {
            LogDebug($"[MESH] No mesh path found for character '{character}'");
            return null;
        }

        // Preview meshes skip texture/material resolution entirely (flat-grey fallback in
        // the viewer); callers that need textures (thumbnail baking) pass includeMaterials.
        return LoadMeshFromPath(meshPath, includeMaterials: false);
    }

    public MeshJsonData? LoadMeshFromPath(string meshPath, bool includeMaterials)
    {
        if (_provider == null) return null;

        // Separate cache entries per variant: the preview must never receive a textured
        // entry and thumbnail baking must never receive a texture-less one.
        string cacheKey = includeMaterials ? meshPath + "|mat" : meshPath;

        lock (_meshCacheSync)
        {
            if (_meshCache.TryGetValue(cacheKey, out var entry))
            {
                _meshCacheOrder.Remove(entry.node);
                _meshCacheOrder.AddFirst(entry.node);
                return entry.data;
            }
        }

        var result = BuildMesh(meshPath, includeMaterials);
        if (result == null) return null;

        lock (_meshCacheSync)
        {
            var node = _meshCacheOrder.AddFirst(cacheKey);
            _meshCache[cacheKey] = (node, result);
            while (_meshCache.Count > MeshCacheMax && _meshCacheOrder.Last != null)
            {
                _meshCache.Remove(_meshCacheOrder.Last.Value);
                _meshCacheOrder.RemoveLast();
            }
        }
        return result;
    }

    private MeshJsonData? BuildMesh(string meshPath, bool includeMaterials)
    {
        if (_provider == null) return null;

        try
        {
            var obj = _provider.SafeLoadPackageObject<USkeletalMesh>(meshPath);
            if (obj == null)
            {
                LogDebug($"[MESH] Failed to load package: {meshPath}");
                return null;
            }

            if (!obj.TryConvert(out var convertedMesh))
            {
                LogDebug($"[MESH] TryConvert failed: {meshPath}");
                return null;
            }

            var lod = convertedMesh.LODs.FirstOrDefault();
            if (lod == null) return null;

            var skelLod = lod as CSkelMeshLod;
            if (skelLod == null) return null;

            var vertexCount = skelLod.Verts.Length;
            var positions = new float[vertexCount * 3];
            var normals = new float[vertexCount * 3];
            var uvs = new float[vertexCount * 2];
            var skinIndicesArr = new int[vertexCount * 4];
            var skinWeightsArr = new float[vertexCount * 4];

            for (int i = 0; i < vertexCount; i++)
            {
                var v = skelLod.Verts[i];
                positions[i * 3] = v.Position.X;
                positions[i * 3 + 1] = v.Position.Z;
                positions[i * 3 + 2] = v.Position.Y;

                normals[i * 3] = v.Normal.X;
                normals[i * 3 + 1] = v.Normal.Z;
                normals[i * 3 + 2] = v.Normal.Y;

                uvs[i * 2] = v.UV.U;
                uvs[i * 2 + 1] = 1.0f - v.UV.V;

                int infCount = Math.Min(4, v.Influences.Count);
                float totalWeight = 0;
                for (int j = 0; j < infCount; j++)
                {
                    skinIndicesArr[i * 4 + j] = v.Influences[j].Bone;
                    skinWeightsArr[i * 4 + j] = v.Influences[j].Weight;
                    totalWeight += v.Influences[j].Weight;
                }
                if (totalWeight > 0)
                {
                    for (int j = 0; j < infCount; j++)
                        skinWeightsArr[i * 4 + j] /= totalWeight;
                }
            }

            var allIndices = new List<int>();
            foreach (var idx in skelLod.Indices.Value)
            {
                allIndices.Add((int)idx);
            }

            var skeleton = convertedMesh.RefSkeleton;
            var boneNames = new string[skeleton.Count];
            var boneParents = new int[skeleton.Count];
            var bindPoseArr = new float[skeleton.Count * 7];

            for (int i = 0; i < skeleton.Count; i++)
            {
                var bone = skeleton[i];
                boneNames[i] = bone.Name.Text;
                boneParents[i] = bone.ParentIndex;

                bindPoseArr[i * 7 + 0] = bone.Position.X;
                bindPoseArr[i * 7 + 1] = bone.Position.Z;
                bindPoseArr[i * 7 + 2] = bone.Position.Y;
                bindPoseArr[i * 7 + 3] = -bone.Orientation.X;
                bindPoseArr[i * 7 + 4] = -bone.Orientation.Z;
                bindPoseArr[i * 7 + 5] = -bone.Orientation.Y;
                bindPoseArr[i * 7 + 6] = bone.Orientation.W;
            }

            LogDebug($"[MESH] Loaded from {meshPath} ({vertexCount} verts, {skeleton.Count} bones)");
            return new MeshJsonData
            {
                positions = positions,
                normals = normals,
                uvs = uvs,
                indices = allIndices.ToArray(),
                skinIndices = skinIndicesArr,
                skinWeights = skinWeightsArr,
                boneNames = boneNames,
                bindPose = bindPoseArr,
                boneParents = boneParents,
                materials = includeMaterials ? ResolveMaterials(skelLod) : null
            };
        }
        catch (Exception ex)
        {
            LogDebug($"[MESH] Error loading mesh from {meshPath}: {ex.Message}");
            return null;
        }
    }

    private List<MeshMaterialGroup>? ResolveMaterials(CSkelMeshLod skelLod)
    {
        try
        {
            var resolver = _texResolver;
            if (resolver == null) return null;

            var sections = skelLod.Sections?.Value;
            if (sections == null || sections.Length == 0) return null;

            var input = new List<(string material, int firstIndex, int numFaces)>(sections.Length);
            foreach (var s in sections)
                input.Add((s.Material?.ToString() ?? "", s.FirstIndex, s.NumFaces));

            return resolver.ResolveTextureGroups(input);
        }
        catch (Exception ex)
        {
            LogDebug($"[MESH] texture resolve failed: {ex.Message}");
            return null;
        }
    }

    private string? FindMeshPath(string character)
    {
        lock (_findMeshPathSync)
        {
            if (_findMeshPathMemo.TryGetValue(character, out var cached)) return cached;
        }
        var result = FindMeshPathUncached(character);
        lock (_findMeshPathSync)
        {
            _findMeshPathMemo[character] = result;
        }
        return result;
    }

    private string? FindMeshPathUncached(string character)
    {
        try
        {
            var charactersDir = Path.Combine(ContentDir, "Characters");
            if (!Directory.Exists(charactersDir)) return null;

            var searchPatterns = new[]
            {
                $"SK_*{character}*",
                $"SK_Puppet_{character}*"
            };

            foreach (var pattern in searchPatterns)
            {
                var files = Directory.GetFiles(charactersDir, pattern + ".uasset", SearchOption.AllDirectories);
                var match = files.FirstOrDefault(f => !f.Contains("PhysicsAsset") && !f.Contains("Cloth") && !f.Contains("Cine") && !f.Contains("Skeleton"));
                if (match != null)
                {
                    var relPath = match.Substring(_contentPath.Length).TrimStart('\\', '/').Replace('\\', '/');
                    relPath = relPath.Replace(".uasset", "");
                    return "Game/" + relPath;
                }
            }

            var fallback = Directory.GetFiles(charactersDir, $"SK_{character}*.uasset", SearchOption.AllDirectories)
                .FirstOrDefault(f => !f.Contains("PhysicsAsset") && !f.Contains("Cloth") && !f.Contains("Cine") && !f.Contains("Skeleton"));
            if (fallback != null)
            {
                var relPath = fallback.Substring(_contentPath.Length).TrimStart('\\', '/').Replace('\\', '/');
                relPath = relPath.Replace(".uasset", "");
                return "Game/" + relPath;
            }
        }
        catch { }
        return null;
    }

    public string ToJson(AnimationJsonData data)
    {
        return JsonConvert.SerializeObject(data, Formatting.None);
    }

    public string ToJson(MeshJsonData data)
    {
        return JsonConvert.SerializeObject(data, Formatting.None);
    }

    public ComboGraph? LoadArchetypeAttackGraph(string arch)
    {
        return LoadArchetypeAttackGraph(arch, "Normal");
    }

    public ComboGraph? LoadArchetypeAttackGraph(string arch, string difficulty)
    {
        try
        {
            var attacksDir = Path.Combine(ContentDir, "DB", "AI", "Archetypes", arch, "Attacks");
            if (!Directory.Exists(attacksDir)) return null;
            var files = Directory.GetFiles(attacksDir, "*.uasset", SearchOption.TopDirectoryOnly)
                .Where(f=>!Path.GetFileName(f).Contains("HitBoxData", StringComparison.OrdinalIgnoreCase)).ToList();
            files.AddRange(Directory.GetFiles(attacksDir, "*.uasset", SearchOption.AllDirectories)
                .Where(f=>!Path.GetFileName(f).Contains("HitBoxData", StringComparison.OrdinalIgnoreCase) && !files.Contains(f)));
            if (difficulty == "Hard")
                files = files.Where(f=>Path.GetFileName(f).Contains("Hard", StringComparison.OrdinalIgnoreCase)).ToList();
            else
                files = files.Where(f=>!Path.GetFileName(f).Contains("Hard", StringComparison.OrdinalIgnoreCase)).ToList();
            if (files.Count==0) return null;
            var rows = new List<(string name, string anim)>();
            foreach (var f in files.Distinct())
            {
                var rel = f.Substring(ContentDir.Length).TrimStart('\\','/').Replace('\\','/');
                rel = rel.Replace(".uasset","");
                var gamePath = "Game/" + rel;
                var anim = ResolveAnimationFromDB(gamePath);
                if (!string.IsNullOrEmpty(anim))
                {
                    var name = Path.GetFileNameWithoutExtension(f);
                    rows.Add((name, anim));
                }
            }
            if (rows.Count==0) return null;
            rows = rows.OrderBy(r=>r.name).ToList();
            var graph = new ComboGraph{ WeaponName=arch };
            // Split into multiple linear chains (pools) by AI condition — each 8-row chunk is a pool/chain (Distance <300, <100, Counter)
            // Example model: Distance <300 chain1 (0-7) + chain2 (8-15), Distance <100 chain (16-23), Counter (24+)
            string[] poolConds = { "Distance < 300", "Distance < 300", "Distance < 100", "Counter (HasJustParried)" };
            for (int i=0;i<rows.Count;i++)
            {
                var (nm, ap) = rows[i];
                int poolIdx = Math.Min(i / 8, poolConds.Length-1);
                string cond = poolConds[poolIdx];
                graph.Nodes.Add(new ComboNode{ Id=i, TreeIndex=i, Name=nm, AnimPath=ap, DefaultAnimPath=ap, DefaultDBPath=$"Game/DB/AI/Archetypes/{arch}/Attacks/{nm}", SourceDBPath=$"Game/DB/AI/Archetypes/{arch}/Attacks/{nm}", DisplayName=nm, IsRoot=false, DirectionLabel=cond });
                if (i>0 && i % 8 != 0) graph.Edges.Add(new ComboEdge{ FromNodeId=i-1, ToNodeId=i, InputName="Auto"});
            }
            return graph;
        } catch { return null; }
    }

    public void Dispose()
    {
        try { (_overlayProvider as IDisposable)?.Dispose(); } catch { }
        _overlayProvider = null;
        try { (_provider as IDisposable)?.Dispose(); } catch { }
        _provider = null;
    }
}
