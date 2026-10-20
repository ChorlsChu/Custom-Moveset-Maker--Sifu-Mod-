using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SifuMovesetEditor;

/// <summary>
/// One node's condition + chance data: transition rows (m_Transitions m_fProbability)
/// with their condition classes/params, an editable Condition-edits card (param values
/// per condition instance + outcome re-route + clear), plus the node's own
/// m_ConditionalAttacks and m_RequiredTags view-only sections. Rows come from the
/// parse-time TreeRows snapshot plus every new link whose export writeback targets the
/// same struct (A' owner rule, so an attack and its owning redirect show the same set).
/// Displayed chance values are the effective/export values; edits apply to a copy - OK
/// commits, Cancel discards. Clearing a condition also syncs the entry back to the
/// node's forward links: an outcome the collapse drops but the node still links to
/// becomes its own chance row (auto-weighted mixup, staged until OK). For MainChar
/// everything is view-only.
/// </summary>
public partial class BranchChancesDialog : Window
{
    internal sealed class ChanceRow
    {
        public int RowIndex;               // asset row index; -1 = new-link row (appended on export)
        public string InputName = "";
        public string TargetSummary = "";
        public int KeyCount = 1;
        public bool ConditionPresent;
        public bool VanillaHadCondition;
        public bool VanillaInstanceEmpty;   // vanilla row already carried an empty m_ConditionInstance ref
        public string ConditionClass = "";
        public List<string> ConditionParams = new();
        public int ConditionInstanceIndex; // m_ConditionInstance FPackageIndex (0 = none)
        public List<ConditionParamInfo>? ConditionParamList;
        public List<byte> RowKeys = new();           // m_TargetNodes outcome keys (vanilla)
        public List<int> RowVanillaTargets = new();  // parallel vanilla targets
        public bool IsNewLink;
        public ComboEdge? Edge;
        public float Original;             // vanilla probability (new rows: weight placeholder)
        public float? Explicit;            // stored user value (null = automatic)
        public float Effective;            // displayed value (export writes this)
        public TextBox? Field;
    }

    private readonly ComboGraph _graph;
    private readonly ComboNode _node;
    private readonly int _structIdx;
    private readonly bool _vanillaAvailable;
    private readonly bool _canEditChances;
    private readonly string? _contentDir;
    private static List<ConditionInfo>? _condCatalog;
    private bool _catalogScanning;
    private readonly List<ChanceRow> _rows = new();
    private bool _syncing;

    // Local condition-edit state: condition export -> (path -> value), and this
    // struct's row edits (lazy - only touched rows). Seeded from the graph dicts;
    // OK builds fresh graph dicts from them.
    private readonly Dictionary<int, Dictionary<string, string>> _condParamEdits = new();
    private readonly Dictionary<int, TreeRowConditionEdit> _condRowEdits = new();
    // Same, for rows that only exist after export (new links / appended-node rows),
    // keyed by edge key.
    private readonly Dictionary<string, TreeRowConditionEdit> _edgeCondEdits = new();
    // Mixup links the clear/restore sync creates or drops this session - staged so
    // Cancel leaves the graph untouched; OK merges them in OpenBranchChances.
    private readonly List<ComboEdge> _stagedEdges = new();
    private readonly List<ComboEdge> _removedMixupEdges = new();
    // Link merges staged this session: value = (row, key) the link joins, null =
    // unmerge (the link returns to its own appended chance row). The AddedKeys side
    // lives in _condRowEdits (local clones); the edge fields change only at OK.
    private readonly Dictionary<ComboEdge, (int rowIdx, int key)?> _stagedMerges = new();

    /// <summary>Fresh override dict for commit (clone + this struct's rows). Null until OK.</summary>
    public Dictionary<int, Dictionary<int, float>>? ResultOverrides { get; private set; }
    /// <summary>Edge probability assignments for this struct's new-link rows (null = auto).</summary>
    public List<(ComboEdge edge, float? probability)>? ResultEdgeProbabilities { get; private set; }
    /// <summary>Fresh condition-param override dict for commit (clone + touched instances).</summary>
    public Dictionary<int, Dictionary<string, string>>? ResultConditionParams { get; private set; }
    /// <summary>Fresh row-condition override dict for commit (clone + this struct's rows).</summary>
    public Dictionary<int, Dictionary<int, TreeRowConditionEdit>>? ResultRowConditions { get; private set; }
    /// <summary>Fresh edge-condition override dict for commit (clone + touched edge rows).</summary>
    public Dictionary<string, TreeRowConditionEdit>? ResultEdgeConditions { get; private set; }
    /// <summary>Mixup links created while syncing cleared entries - added to the graph on OK.</summary>
    public List<ComboEdge> ResultNewEdges { get; private set; } = new();
    /// <summary>Mixup links dropped by restore/re-sync - removed from the graph on OK.</summary>
    public List<ComboEdge> ResultRemovedEdges { get; private set; } = new();
    /// <summary>Links merged into an existing conditioned row this session - the edge
    /// fields are written on OK (this dialog's struct, staged row, outcome key).</summary>
    public List<(ComboEdge edge, int structIdx, int rowIdx, int key)>? ResultMergedEdges { get; private set; }
    /// <summary>Merges undone this session - edges return to their own appended rows.</summary>
    public List<ComboEdge>? ResultUnmergedEdges { get; private set; }

    /// <summary>Working state frozen when an outcome combo's ⌖ pick suspends the dialog
    /// (close-carry-reopen, same pattern as Start Entries). Carries the working rows and
    /// every local edit store so the reopened instance renders exactly what closed.</summary>
    internal sealed class PickSuspendState
    {
        public bool VanillaAvailable;
        public List<ChanceRow> Rows = new();
        public Dictionary<int, TreeRowConditionEdit> RowEdits = new();
        public Dictionary<string, TreeRowConditionEdit> EdgeEdits = new();
        public Dictionary<int, Dictionary<string, string>> CondParamEdits = new();
        public List<ComboEdge> StagedEdges = new();
        public List<ComboEdge> RemovedMixupEdges = new();
        public Dictionary<ComboEdge, (int rowIdx, int key)?> StagedMerges = new();
        public ChanceRow Row = null!;
        public byte Key;
    }

    /// <summary>Set when the dialog closed to pick a target on the canvas; MainWindow
    /// reads the pick request, enters pick mode, and reopens with <see cref="PickSuspend"/>.</summary>
    public bool SuspendedForPick { get; private set; }
    internal PickSuspendState? PickSuspend { get; private set; }

    private static readonly Brush GrayBrush = new SolidColorBrush(Color.FromRgb(0x6c, 0x70, 0x86));
    private static readonly Brush DimBrush = new SolidColorBrush(Color.FromRgb(0xa6, 0xad, 0xc8));
    private static readonly Brush TextBrush = new SolidColorBrush(Color.FromRgb(0xcd, 0xd6, 0xf4));
    private static readonly Brush BlueBrush = new SolidColorBrush(Color.FromRgb(0x89, 0xb4, 0xfa));
    private static readonly Brush GreenBrush = new SolidColorBrush(Color.FromRgb(0xa6, 0xe3, 0xa1));
    private static readonly Brush RedBrush = new SolidColorBrush(Color.FromRgb(0xf3, 0x8b, 0xa8));
    private static readonly Brush WarnBrush = new SolidColorBrush(Color.FromRgb(0xf9, 0xe2, 0xaf));
    private static readonly Brush CardBrush = new SolidColorBrush(Color.FromRgb(0x18, 0x18, 0x25));
    private static readonly Brush HeadBrush = new SolidColorBrush(Color.FromRgb(0x31, 0x32, 0x44));
    private static readonly Brush BorderBrushDark = new SolidColorBrush(Color.FromRgb(0x31, 0x32, 0x44));
    private static readonly Brush BorderBrushInput = new SolidColorBrush(Color.FromRgb(0x45, 0x47, 0x5a));

    // Internal (not public): the resume param type is internal - MainWindow is the only
    // construction site and lives in this assembly.
    internal BranchChancesDialog(ComboGraph graph, ComboNode node, bool canEditChances,
        string? contentDir = null, PickSuspendState? resumeState = null)
    {
        InitializeComponent();
        _graph = graph;
        _node = node;
        _canEditChances = canEditChances;
        _contentDir = contentDir;
        _structIdx = node.TreeIndex < 0 ? -1 : TreeChanceMath.ResolveWriteStruct(graph, node);

        // Resume after a pick-on-graph suspend: the previous instance froze its working
        // rows and edit stores into the state - adopt them and rebuild, skipping the
        // vanilla/edge row construction and the graph-edit hydration (already in the
        // carried stores).
        if (resumeState != null)
        {
            _vanillaAvailable = resumeState.VanillaAvailable;
            _rows.AddRange(resumeState.Rows);
            foreach (var kv in resumeState.RowEdits) _condRowEdits[kv.Key] = kv.Value;
            foreach (var kv in resumeState.EdgeEdits) _edgeCondEdits[kv.Key] = kv.Value;
            foreach (var kv in resumeState.CondParamEdits) _condParamEdits[kv.Key] = kv.Value;
            _stagedEdges.AddRange(resumeState.StagedEdges);
            _removedMixupEdges.AddRange(resumeState.RemovedMixupEdges);
            foreach (var kv in resumeState.StagedMerges) _stagedMerges[kv.Key] = kv.Value;
            RefreshSubtitle();
            KickCatalogScan();
            Rebuild();
            RefreshGlobalStatus();
            return;
        }

        List<TreeRowInfo> vrows = new();
        if (_structIdx >= 0 && graph.TreeRows.TryGetValue(_structIdx, out var parsedRows))
            vrows = parsedRows;
        _vanillaAvailable = vrows.Count > 0;

        if (_vanillaAvailable)
        {
            graph.TreeRowProbabilityOverrides.TryGetValue(_structIdx, out var structOverrides);
            foreach (var vr in vrows)
            {
                float? expl = null;
                if (structOverrides != null && structOverrides.TryGetValue(vr.RowIndex, out var ov))
                    expl = ov;
                _rows.Add(new ChanceRow
                {
                    RowIndex = vr.RowIndex,
                    InputName = vr.InputName,
                    KeyCount = vr.KeyCount,
                    ConditionPresent = vr.ConditionPresent,
                    VanillaHadCondition = vr.ConditionPresent,
                    VanillaInstanceEmpty = vr.ConditionPresent && vr.ConditionInstanceIndex == 0,
                    ConditionClass = vr.ConditionClass,
                    ConditionParams = new List<string>(vr.ConditionParams),
                    ConditionInstanceIndex = vr.ConditionInstanceIndex,
                    ConditionParamList = vr.ConditionParamList,
                    RowKeys = new List<byte>(vr.Keys),
                    RowVanillaTargets = new List<int>(vr.TargetTreeIndices),
                    Original = vr.OriginalProbability,
                    Explicit = expl,
                    TargetSummary = DescribeTargets(vr)
                });
            }
        }

        // New links whose export writeback targets this struct - the same rows the export
        // rebalance sees (Edges order = append order). Custom nodes own only their own links.
        // Merged links get no chance row - their outcome key rides the target row's routing.
        foreach (var e in graph.Edges)
        {
            if (!e.IsNewLink || e.IsRedirect || e.MergedIntoStruct >= 0) continue;
            var from = graph.Nodes.FirstOrDefault(n => n.Id == e.FromNodeId);
            if (from == null) continue;
            if (_structIdx < 0)
            {
                if (e.FromNodeId != node.Id) continue;
            }
            else if (TreeChanceMath.ResolveWriteStruct(graph, from) != _structIdx) continue;

            _rows.Add(MakeEdgeRow(e));
        }

        RefreshSubtitle();
        HydrateConditionOverrides();
        KickCatalogScan();
        Recompute();
        Rebuild();
        RefreshGlobalStatus();
    }

    /// <summary>Header line - row count moves when the clear/restore sync adds or drops
    /// mixup rows, so Rebuild repaints it too.</summary>
    private void RefreshSubtitle()
    {
        string structLabel = _structIdx >= 0 ? $"tree[{_structIdx}]" : "new node";
        string structName = _structIdx >= 0 ? StructDisplay(_structIdx) : NodeDisplay(_node);
        txtSubtitle.Text = $"{structName} · {structLabel} · {_rows.Count} row{(_rows.Count == 1 ? "" : "s")} · {_graph.WeaponName}";
    }

    /// <summary>Rows are built from the vanilla parse snapshot, so condition edits saved
    /// in earlier sessions - instances added onto previously-unconditional rows, class
    /// swaps, and conditions on new-link rows - must be re-applied here or the dialog
    /// renders them unconditioned on reopen even though the graph still holds them.
    /// Clear-only edits need no hydration: the snapshot already flags those rows and
    /// RowEdit lazy-seeds the clear state.</summary>
    private void HydrateConditionOverrides()
    {
        if (_structIdx >= 0 && _graph.TreeRowConditionOverrides.TryGetValue(_structIdx, out var map))
        {
            foreach (var r in _rows)
            {
                if (r.Edge != null || r.RowIndex < 0) continue;
                if (map.TryGetValue(r.RowIndex, out var e) && e.HasPendingInstance)
                    MarkConditioned(r, e);
            }
        }
        foreach (var r in _rows)
        {
            if (r.Edge == null) continue;
            if (_graph.TreeEdgeConditionOverrides.TryGetValue(ComboGraph.EdgeConditionKey(r.Edge), out var e)
                && e.HasPendingInstance)
                MarkConditioned(r, e);
        }
    }

    /// <summary>Restores a row's conditioned display from a saved edit: class, instance
    /// ref, param list (same-file row in this file, else the catalog example) and - for
    /// rows that were unconditional in vanilla - the outcome keyset the edit carries
    /// (same seeding a fresh pick applies).</summary>
    private void MarkConditioned(ChanceRow r, TreeRowConditionEdit e)
    {
        string cls = e.CloneClass;
        List<ConditionParamInfo>? plist = null;
        if (e.SetInstance != 0)
        {
            foreach (var rows in _graph.TreeRows.Values)
            {
                var vr = rows.FirstOrDefault(x => x.ConditionInstanceIndex == e.SetInstance);
                if (vr == null) continue;
                if (string.IsNullOrEmpty(cls)) cls = vr.ConditionClass;
                if (plist == null && vr.ConditionParamList is { Count: > 0 })
                    plist = vr.ConditionParamList;
            }
        }
        else if (!string.IsNullOrEmpty(e.CloneSourceRel))
        {
            var ci = _condCatalog?.FirstOrDefault(c =>
                string.Equals(c.SourceComboRel, e.CloneSourceRel, StringComparison.OrdinalIgnoreCase)
                && c.SourceExportIndex == e.CloneSourceExport);
            if (ci != null)
            {
                if (string.IsNullOrEmpty(cls)) cls = ci.ClassName;
                plist = ci.Params;
            }
        }
        r.ConditionPresent = true;
        r.ConditionClass = cls;
        r.ConditionInstanceIndex = e.SetInstance != 0
            ? e.SetInstance
            : TreeRowConditionEdit.CloneVirtualKey(e.CloneSourceRel, e.CloneSourceExport);
        r.ConditionParamList = plist;
        if (!r.VanillaHadCondition)
        {
            // Union both retarget dicts - a key routed to a user node lives only in
            // RetargetNodeIds, so reading Retarget alone drops those outcome lines
            // from the reopened display (the data was never lost, just not shown).
            var keys = e.Retarget.Count > 0 || e.RetargetNodeIds.Count > 0
                ? e.Retarget.Keys.Concat(e.RetargetNodeIds.Keys).Distinct().OrderBy(k => k).ToList()
                : new List<byte> { 0, 1 };
            r.RowKeys = keys;
            if (r.Edge != null)
                r.RowVanillaTargets = keys.Select(_ => TreeRowConditionEdit.TargetNatural).ToList();
            else
            {
                // Unconditional vanilla row: one 255 target shared by every outcome.
                int origT = r.RowVanillaTargets.Count > 0 ? r.RowVanillaTargets[0] : -1;
                r.RowVanillaTargets = keys.Select(_ => origT).ToList();
            }
        }
    }

    /// <summary>First dialog open kicks a background scan of every combo file so the
    /// add-condition picker lists every condition class used across all enemy units
    /// without a manual click; the static catalog keeps it one scan per session.</summary>
    private void KickCatalogScan()
    {
        if (_condCatalog != null || _catalogScanning || string.IsNullOrEmpty(_contentDir)) return;
        _catalogScanning = true;
        string dir = _contentDir!;
        _ = System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                var found = ConditionCatalog.Scan(dir, null);
                Dispatcher.Invoke(() =>
                {
                    _condCatalog = found;
                    _catalogScanning = false;
                    Rebuild();
                });
            }
            catch (Exception ex)
            {
                ErrorLog.Write("COND-CATALOG", ex);
                Dispatcher.Invoke(() => _catalogScanning = false);
            }
        });
    }

    private string StructDisplay(int treeIdx)
    {
        var n = _graph.Nodes.FirstOrDefault(x => x.TreeIndex == treeIdx && !x.IsRedirect)
            ?? _graph.Nodes.FirstOrDefault(x => x.TreeIndex == treeIdx);
        return n != null ? NodeDisplay(n) : $"tree[{treeIdx}]";
    }

    private static string NodeDisplay(ComboNode n)
        => !string.IsNullOrEmpty(n.DisplayName) ? n.DisplayName
        : !string.IsNullOrEmpty(n.Name) ? n.Name : $"#{n.Id}";

    private string DescribeTargets(TreeRowInfo vr)
    {
        if (vr.TargetTreeIndices.Count == 0) return "(no targets)";
        var parts = new List<string>(vr.TargetTreeIndices.Count);
        foreach (var kv in vr.TargetTreeIndices.Select((t, i) => (t, i)))
        {
            var tn = _graph.Nodes.FirstOrDefault(n => n.TreeIndex == kv.t);
            string name = tn != null ? NodeDisplay(tn) : $"tree[{kv.t}]";
            parts.Add(vr.KeyCount > 1 ? $"{(kv.i < vr.Keys.Count ? vr.Keys[kv.i].ToString() : "?")}→{name}" : name);
        }
        return string.Join(", ", parts);
    }

    /// <summary>TargetSummary from the row's POST-override targets (clear/retarget
    /// edits applied) - mirrors what export writes, so a retarget data-swap shows the
    /// destination instead of the redirect. Duplicates collapse (a swap can funnel
    /// several keys onto one target).</summary>
    private string DescribeEffectiveTargets(ChanceRow r)
    {
        var targets = RowPostTargets(r);
        if (targets.Count == 0) return "(no targets)";
        var parts = new List<string>(targets.Count);
        foreach (var t in targets.Distinct())
        {
            var tn = _graph.Nodes.FirstOrDefault(n => n.TreeIndex == t);
            parts.Add(tn != null ? NodeDisplay(tn) : $"tree[{t}]");
        }
        return string.Join(", ", parts);
    }

    /// <summary>Effective values: typed rows exact; automatic rows keep raw values with no
    /// append, or share the remainder once new links append (mirrors export).</summary>
    private void Recompute()
    {
        bool hasAppends = _rows.Any(r => r.IsNewLink);
        if (!hasAppends)
        {
            foreach (var r in _rows) r.Effective = r.Explicit ?? r.Original;
            return;
        }

        var vanillaChance = new List<TreeChanceMath.Row>();
        foreach (var r in _rows.Where(r => !r.IsNewLink))
            vanillaChance.Add(new TreeChanceMath.Row(r.Explicit, r.Original));
        float newWeight = TreeChanceMath.NewRowAutoWeight(vanillaChance);

        var chance = new List<TreeChanceMath.Row>(_rows.Count);
        foreach (var r in _rows)
            chance.Add(r.IsNewLink
                ? new TreeChanceMath.Row(r.Explicit, newWeight)
                : new TreeChanceMath.Row(r.Explicit, r.Original));
        var vals = TreeChanceMath.Compute(chance, normalize: true);
        for (int i = 0; i < _rows.Count; i++) _rows[i].Effective = vals[i];
    }

    private void Rebuild()
    {
        // TargetSummary is frozen from the vanilla snapshot at row build - re-derive
        // it from the post-override targets so retarget data-swaps and pick changes
        // read correctly (the destination, not the redirect) before repainting.
        foreach (var r in _rows)
            r.TargetSummary = r.Edge != null
                ? r.TargetSummary // link rows always serve the link's own target
                : DescribeEffectiveTargets(r);
        rowsPanel.Children.Clear();
        // Add/change card first: it is the primary action, and the chances card below it
        // is tall enough to bury anything appended at the end of the scroll area.
        foreach (var sec in BuildAddConditionSections())
            rowsPanel.Children.Add(sec);
        if (_rows.Count == 0)
        {
            rowsPanel.Children.Add(new TextBlock
            {
                Text = _structIdx >= 0 && !_vanillaAvailable
                    ? "Vanilla transition rows aren't stored in this save - existing rows export unchanged; new-link chances below still apply."
                    : "No transition rows yet - add a new link from this node first.",
                Foreground = WarnBrush,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(4, 6, 4, 6)
            });
        }
        else
        {
            rowsPanel.Children.Add(BuildCard());
        }
        foreach (var sec in BuildConditionEditSections())
            rowsPanel.Children.Add(sec);
        foreach (var sec in BuildConditionSections())
            rowsPanel.Children.Add(sec);
        RefreshSubtitle();
        RefreshGlobalStatus();
    }

    /// <summary>Local param-edit dict for a condition instance (lazily seeded from the
    /// graph so rows sharing one instance stay consistent).</summary>
    private Dictionary<string, string> ParamEdits(int expIdx)
    {
        if (!_condParamEdits.TryGetValue(expIdx, out var d))
        {
            d = new Dictionary<string, string>();
            if (_graph.TreeConditionParamOverrides.TryGetValue(expIdx, out var stored))
                foreach (var kv in stored) d[kv.Key] = kv.Value;
            _condParamEdits[expIdx] = d;
        }
        return d;
    }

    /// <summary>Local row-condition edit (lazily seeded from the graph).</summary>
    private TreeRowConditionEdit RowEdit(int rowIdx)
    {
        if (!_condRowEdits.TryGetValue(rowIdx, out var e))
        {
            e = _structIdx >= 0 && _graph.TreeRowConditionOverrides.TryGetValue(_structIdx, out var map)
                && map.TryGetValue(rowIdx, out var stored)
                ? stored.Clone()
                : new TreeRowConditionEdit();
            _condRowEdits[rowIdx] = e;
        }
        return e;
    }

    /// <summary>Local edge-condition edit for an export-appended row (lazily seeded).</summary>
    private TreeRowConditionEdit EdgeEdit(ComboEdge e)
    {
        string key = ComboGraph.EdgeConditionKey(e);
        if (!_edgeCondEdits.TryGetValue(key, out var edit))
        {
            edit = _graph.TreeEdgeConditionOverrides.TryGetValue(key, out var stored)
                ? stored.Clone()
                : new TreeRowConditionEdit();
            _edgeCondEdits[key] = edit;
        }
        return edit;
    }

    /// <summary>Condition edit backing a row - vanilla rows are struct/row keyed, rows
    /// appended at export are edge keyed.</summary>
    private TreeRowConditionEdit EditFor(ChanceRow r)
        => r.Edge != null ? EdgeEdit(r.Edge) : RowEdit(r.RowIndex);

    /// <summary>Display name for an edge endpoint node.</summary>
    private string EdgeNodeName(int nodeId)
    {
        var n = _graph.Nodes.FirstOrDefault(x => x.Id == nodeId);
        return n != null ? NodeDisplay(n) : $"#{nodeId}";
    }

    /// <summary>Row label: asset row number for vanilla rows, "from → to" for rows that
    /// only exist after export.</summary>
    private string RowTitle(ChanceRow r)
        => r.Edge != null
            ? $"link {EdgeNodeName(r.Edge.FromNodeId)} → {EdgeNodeName(r.Edge.ToNodeId)}"
            : $"Row {r.RowIndex + 1}";

    /// <summary>Chance-row wrapper for a new link (constructor + unmerge re-add).</summary>
    private ChanceRow MakeEdgeRow(ComboEdge e)
    {
        var to = _graph.Nodes.FirstOrDefault(n => n.Id == e.ToNodeId);
        return new ChanceRow
        {
            RowIndex = -1,
            InputName = e.InputName,
            TargetSummary = to != null ? NodeDisplay(to) : $"#{e.ToNodeId}",
            Original = 0f,
            Explicit = e.NewLinkProbability,
            IsNewLink = true,
            Edge = e
        };
    }

    /// <summary>Rows an edge's target may join: conditioned (vanilla or added this
    /// session), not cleared, not a vanilla empty-ref row (nothing evaluates a condition
    /// there). All belong to this dialog's struct, so a merge writes MergedIntoStruct
    /// = _structIdx.</summary>
    private List<ChanceRow> MergeTargets() => _rows
        .Where(r => r.Edge == null && r.RowIndex >= 0 && r.ConditionPresent
            && !r.VanillaInstanceEmpty && !EditFor(r).ClearCondition)
        .ToList();

    /// <summary>Merge a new link into an existing conditioned row: the link's target
    /// joins the row's m_TargetNodes as the lowest free outcome key - no standalone
    /// chance row, no second condition instance. Any condition staged on the link dies
    /// with it (the target row's own condition is what evaluates). Cancel-safe: the
    /// AddedKeys side lives in local edits, the edge fields change only at OK.</summary>
    private void MergeEdgeIntoRow(ChanceRow edgeRow, ChanceRow targetRow)
    {
        var edge = edgeRow.Edge;
        if (edge == null || targetRow.RowIndex < 0 || !_graph.Edges.Contains(edge)) return;
        var tedit = RowEdit(targetRow.RowIndex);
        var used = new HashSet<byte>(targetRow.RowKeys);
        foreach (var k in tedit.AddedKeys) used.Add(k.Key);
        int key = 0;
        while (key < 255 && used.Contains((byte)key)) key++;
        if (key >= 255) return;   // a row cannot hold 255 outcome keys - never happens
        _edgeCondEdits.Remove(ComboGraph.EdgeConditionKey(edge));
        tedit.AddedKeys[(byte)key] = edge.ToNodeId;
        _stagedMerges[edge] = (targetRow.RowIndex, key);
        _rows.Remove(edgeRow);
        RefreshSubtitle();
        Rebuild();
    }

    /// <summary>Unlink one merged outcome key: the key leaves the row edit and the link
    /// comes back as its own chance row (edge fields reset at OK).</summary>
    private void UnmergeKey(ChanceRow row, byte key)
    {
        DropMerge(row, key);
        RefreshSubtitle();
        Rebuild();
    }

    /// <summary>Row edit loses one merged key; the owning edge (staged this session or
    /// stored on the graph) is staged for unmerge and re-added to _rows.</summary>
    private void DropMerge(ChanceRow row, byte key)
    {
        if (row.RowIndex < 0) return;
        var edit = RowEdit(row.RowIndex);
        if (!edit.AddedKeys.Remove(key)) return;
        var edge = FindMergedEdge(row.RowIndex, key);
        if (edge == null) return;
        _stagedMerges[edge] = null;
        if (_rows.All(r => !ReferenceEquals(r.Edge, edge)))
            _rows.Add(MakeEdgeRow(edge));
    }

    /// <summary>Clear/remove path: every link merged into this row returns to its own
    /// appended row - a row losing its condition cannot keep routing its keys.</summary>
    private void DropMergesInto(ChanceRow row)
    {
        if (row.RowIndex < 0) return;
        var edit = RowEdit(row.RowIndex);
        foreach (var key in edit.AddedKeys.Keys.ToList())
            DropMerge(row, key);
    }

    private ComboEdge? FindMergedEdge(int rowIdx, int key)
    {
        foreach (var kv in _stagedMerges)
            if (kv.Value is { } m && m.rowIdx == rowIdx && m.key == key)
                return kv.Key;
        return _graph.Edges.FirstOrDefault(e => e.MergedIntoStruct == _structIdx
            && e.MergedIntoRow == rowIdx && e.MergedIntoKey == key);
    }

    /// <summary>Outcome keys a freshly conditioned row should carry: the example row's
    /// m_TargetNodes keys minus the unconditional 255, falling back to {0,1}.</summary>
    private static List<byte> NormalizeExampleKeys(List<byte>? raw)
    {
        var keys = (raw ?? new List<byte>()).Where(k => k != 255).Distinct().ToList();
        if (keys.Count == 0) keys.AddRange(new byte[] { 0, 1 });
        return keys;
    }

    /// <summary>Count of condition edits pending vs the graph (status line).</summary>
    private int ConditionEditCount()
    {
        int n = 0;
        foreach (var (expIdx, local) in _condParamEdits)
        {
            _graph.TreeConditionParamOverrides.TryGetValue(expIdx, out var stored);
            stored ??= new Dictionary<string, string>();
            foreach (var kv in local)
                if (!stored.TryGetValue(kv.Key, out var sv) || sv != kv.Value) n++;
            foreach (var kv in stored)
                if (!local.ContainsKey(kv.Key)) n++;
        }
        if (_structIdx >= 0 && _graph.TreeRowConditionOverrides.TryGetValue(_structIdx, out var storedMap))
        {
            foreach (var (rowIdx, edit) in _condRowEdits)
            {
                storedMap.TryGetValue(rowIdx, out var st);
                bool sameClear = (st?.ClearCondition ?? false) == edit.ClearCondition;
                var sr = st?.Retarget ?? new Dictionary<byte, int>();
                int rd = edit.Retarget.Count(kv => !sr.TryGetValue(kv.Key, out var v) || v != kv.Value)
                    + sr.Count(kv => !edit.Retarget.ContainsKey(kv.Key));
                var sn = st?.RetargetNodeIds ?? new Dictionary<byte, int>();
                int rn = edit.RetargetNodeIds.Count(kv => !sn.TryGetValue(kv.Key, out var v) || v != kv.Value)
                    + sn.Count(kv => !edit.RetargetNodeIds.ContainsKey(kv.Key));
                var sa = st?.AddedKeys ?? new Dictionary<byte, int>();
                int ad = edit.AddedKeys.Count(kv => !sa.TryGetValue(kv.Key, out var v) || v != kv.Value)
                    + sa.Count(kv => !edit.AddedKeys.ContainsKey(kv.Key));
                bool sameInstance = (st?.SetInstance ?? 0) == edit.SetInstance
                    && string.Equals(st?.CloneSourceRel ?? "", edit.CloneSourceRel ?? "", StringComparison.Ordinal)
                    && (st?.CloneSourceExport ?? -1) == edit.CloneSourceExport;
                n += (sameClear ? 0 : 1) + rd + rn + ad + (sameInstance ? 0 : 1);
            }
        }
        else
        {
            foreach (var edit in _condRowEdits.Values)
                n += (edit.ClearCondition ? 1 : 0) + edit.Retarget.Count + edit.RetargetNodeIds.Count
                    + edit.AddedKeys.Count + (edit.HasPendingInstance ? 1 : 0);
        }
        foreach (var (key, edit) in _edgeCondEdits)
        {
            _graph.TreeEdgeConditionOverrides.TryGetValue(key, out var st);
            bool sameClear = (st?.ClearCondition ?? false) == edit.ClearCondition;
            var sr = st?.Retarget ?? new Dictionary<byte, int>();
            int rd = edit.Retarget.Count(kv => !sr.TryGetValue(kv.Key, out var v) || v != kv.Value)
                + sr.Count(kv => !edit.Retarget.ContainsKey(kv.Key));
            var sn = st?.RetargetNodeIds ?? new Dictionary<byte, int>();
            int rn = edit.RetargetNodeIds.Count(kv => !sn.TryGetValue(kv.Key, out var v) || v != kv.Value)
                + sn.Count(kv => !edit.RetargetNodeIds.ContainsKey(kv.Key));
            bool sameInstance = (st?.SetInstance ?? 0) == edit.SetInstance
                && string.Equals(st?.CloneSourceRel ?? "", edit.CloneSourceRel ?? "", StringComparison.Ordinal)
                && (st?.CloneSourceExport ?? -1) == edit.CloneSourceExport;
            n += (sameClear ? 0 : 1) + rd + rn + (sameInstance ? 0 : 1);
        }
        return n;
    }

    /// <summary>Editable card for vanilla rows carrying m_ConditionInstance, grouped by
    /// condition instance: shared typed param editors once per instance, then each
    /// referencing row's outcome routing with True/False labels (verified polarity:
    /// key 0 = True, key 1 = False) and a node picker (connected nodes first,
    /// action/event included). Empty without rows.</summary>
    private List<UIElement> BuildConditionEditSections()
    {
        var secs = new List<UIElement>();
        var condRows = _rows.Where(r => r.ConditionPresent && (r.RowIndex >= 0 || r.Edge != null)).ToList();
        if (condRows.Count == 0) return secs;

        bool canEdit = _canEditChances;
        var content = new StackPanel { Margin = new Thickness(10, 4, 10, 8) };
        bool anyStale = false;
        int groups = 0;

        foreach (var grp in condRows.GroupBy(r => r.ConditionInstanceIndex))
        {
            groups++;
            var rows = grp.ToList();
            int expIdx = grp.Key;
            string cls = rows[0].ConditionClass;
            bool anyLive = rows.Any(r => !EditFor(r).ClearCondition);

            var block = new Border
            {
                BorderBrush = BorderBrushInput,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(8, 6, 8, 6),
                Margin = new Thickness(0, 3, 0, 4),
                Background = HeadBrush
            };
            var bp = new StackPanel();

            // group header: class name + shared-instance badge
            var head = new StackPanel { Orientation = Orientation.Horizontal };
            head.Children.Add(new TextBlock
            {
                Text = cls,
                FontWeight = FontWeights.Bold,
                Foreground = WarnBrush,
                FontSize = 11
            });
            head.Children.Add(new TextBlock
            {
                Text = expIdx < 0
                    ? " · new instance (clone from another file at export)"
                    : expIdx != 0
                        ? $" · instance export {expIdx}" + (rows.Count > 1 ? $" · shared by {rows.Count} rows" : "")
                        : " · instance ref empty",
                Foreground = DimBrush,
                FontSize = 11,
                Margin = new Thickness(4, 0, 0, 0),
                ToolTip = expIdx < 0
                    ? "Condition cloned from another combo file when this project exports; parameters below "
                      + "apply to the clone (keyed by its source, so every row sharing this clone shares them)."
                    : expIdx != 0
                        ? "Parameters below are shared by every row that references this instance - edit once, all rows update."
                        : "The condition instance reference is empty on this save; routing still edits normally."
            });
            bp.Children.Add(head);

            // shared param editors (instance level; one edit applies to every referencing row)
            var plist = rows[0].ConditionParamList;
            if (expIdx != 0 && plist is { Count: > 0 })
            {
                var edits = ParamEdits(expIdx);
                foreach (var pi in plist)
                {
                    bool modified = edits.TryGetValue(pi.Path, out var storedV);
                    string current = modified ? storedV! : pi.Value;
                    var prow = new Grid { Margin = new Thickness(0, 3, 0, 0) };
                    prow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    prow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });

                    var pathTb = new TextBlock
                    {
                        Text = pi.Path,
                        Foreground = modified ? WarnBrush : DimBrush,
                        FontSize = 10,
                        VerticalAlignment = VerticalAlignment.Center,
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(4, 0, 6, 0),
                        ToolTip = $"{pi.Path} ({pi.Kind}) - vanilla: {pi.Value}"
                    };
                    Grid.SetColumn(pathTb, 0);
                    prow.Children.Add(pathTb);

                    if (pi.Kind == "ReadOnly")
                    {
                        var valTb = new TextBlock
                        {
                            Text = current,
                            Foreground = GrayBrush,
                            FontSize = 10,
                            VerticalAlignment = VerticalAlignment.Center,
                            TextTrimming = TextTrimming.CharacterEllipsis,
                            ToolTip = current + "  (not editable)"
                        };
                        Grid.SetColumn(valTb, 1);
                        prow.Children.Add(valTb);
                    }
                    else
                    {
                        var box = new TextBox
                        {
                            Style = (Style)FindResource("DarkTextBox"),
                            Text = current,
                            IsEnabled = canEdit && anyLive,
                            FontWeight = modified ? FontWeights.Bold : FontWeights.Normal,
                            ToolTip = $"New value ({pi.Kind}). Vanilla: {pi.Value}. Export parses it to the property's real type."
                        };
                        if (modified) box.BorderBrush = WarnBrush;
                        string path = pi.Path, vanilla = pi.Value;
                        box.TextChanged += (_, _) =>
                        {
                            if (_syncing) return;
                            string trimmed = box.Text.Trim();
                            var ed = ParamEdits(expIdx);
                            if (trimmed.Length == 0 || trimmed == vanilla)
                            {
                                ed.Remove(path);
                                box.BorderBrush = BorderBrushInput;
                            }
                            else
                            {
                                ed[path] = trimmed;
                                box.BorderBrush = WarnBrush;
                            }
                            RefreshGlobalStatus();
                        };
                        Grid.SetColumn(box, 1);
                        prow.Children.Add(box);
                    }
                    bp.Children.Add(prow);
                }
            }
            else if (rows[0].ConditionParams.Count > 0)
            {
                anyStale = true;
                bp.Children.Add(new TextBlock
                {
                    Text = string.Join("\n", rows[0].ConditionParams),
                    Foreground = GrayBrush,
                    FontSize = 10,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(4, 4, 0, 0)
                });
            }

            // per-row: Clear toggle + outcome routing with node pickers
            foreach (var r in rows)
            {
                var edit = EditFor(r);
                var choices = OutcomeChoices(r);
                bool pending = !r.VanillaHadCondition;

                var sub = new Border
                {
                    BorderBrush = BorderBrushDark,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(3),
                    Padding = new Thickness(6, 4, 6, 5),
                    Margin = new Thickness(0, 5, 0, 0),
                    Background = CardBrush
                };
                var sp = new StackPanel();

                var rowHead = new DockPanel();
                // Vanilla empty-ref rows have nothing to clear; show the button only
                // when a pending condition (or a stale saved flag) needs reverting.
                bool vanillaEmpty = r.VanillaInstanceEmpty;
                if (pending || !vanillaEmpty || edit.HasPendingInstance || edit.ClearCondition)
                {
                    var clearBtn = new Button
                    {
                        Content = pending ? "Remove"
                            : vanillaEmpty ? "Revert"
                            : edit.ClearCondition ? "Restore" : "Clear",
                        Style = (Style)FindResource("DarkButton"),
                        FontSize = 10,
                        Padding = new Thickness(8, 2, 8, 2),
                        Margin = new Thickness(6, 0, 0, 0),
                        IsEnabled = canEdit,
                        ToolTip = pending
                            ? "Remove the added condition - this row goes back to vanilla unconditional."
                            : vanillaEmpty
                                ? "Go back to this row's vanilla state - instance ref empty (no condition). "
                                  + "Routing edits above are kept."
                                : edit.ClearCondition
                                    ? "Keep the condition - restores the instance reference and the original outcome map "
                                      + "(any seeded always-route retarget is dropped)."
                                : "Empty m_ConditionInstance on export (vanilla \"instance ref empty\" state): the row "
                                  + "routes unconditionally and its outcome map collapses to one 255 entry "
                                  + "(target = outcome 0's value, else the first). The surviving route stays "
                                  + "editable below - outcomes this node still links to become their own chance "
                                  + "rows (even split), anything else is dropped."
                    };
                    clearBtn.Click += (_, _) =>
                    {
                        if (pending)
                        {
                            DropAddedCondition(r);
                            Rebuild();
                        }
                        else if (vanillaEmpty)
                        {
                            RemoveCondition(r);   // self-heal path - rebuilds itself
                        }
                        else
                        {
                            var e2 = EditFor(r);
                            e2.ClearCondition = !e2.ClearCondition;
                            if (e2.ClearCondition)
                            {
                                // A row losing its condition cannot keep routing merged
                                // links - they return to their own chance rows first.
                                DropMergesInto(r);
                                SeedCollapseTarget(r, e2);
                            }
                            else DropSeededCollapse(e2);
                            SyncMixupRows();
                            Rebuild();
                        }
                    };
                    DockPanel.SetDock(clearBtn, Dock.Right);
                    rowHead.Children.Add(clearBtn);
                }

                var rtitle = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                rtitle.Children.Add(new TextBlock
                {
                    Text = RowTitle(r),
                    FontWeight = FontWeights.Bold,
                    Foreground = pending ? GreenBrush : TextBrush,
                    FontSize = 10
                });
                rtitle.Children.Add(new TextBlock
                {
                    Text = $" · {r.Effective:0.#}%",
                    Foreground = DimBrush,
                    FontSize = 10,
                    Margin = new Thickness(4, 0, 0, 0),
                    ToolTip = "Row probability (chance roll) - edit it in the transition rows above."
                });
                if (pending)
                    rtitle.Children.Add(new TextBlock
                    {
                        Text = " · added condition",
                        Foreground = GreenBrush,
                        FontWeight = FontWeights.Bold,
                        FontSize = 10,
                        Margin = new Thickness(4, 0, 0, 0),
                        ToolTip = r.Edge != null
                            ? "This row is appended when its link exports - the condition below is written with it."
                            : "This row was unconditional in vanilla - the condition below is written at export."
                    });
                else if (edit.ClearCondition && !r.VanillaInstanceEmpty)
                    rtitle.Children.Add(new TextBlock
                    {
                        Text = " → instance ref empty",
                        Foreground = RedBrush,
                        FontWeight = FontWeights.Bold,
                        FontSize = 10,
                        Margin = new Thickness(4, 0, 0, 0),
                        ToolTip = "On export the condition instance reference is emptied - this row becomes "
                          + "vanilla \"instance ref empty\" and routes unconditionally (outcome map collapses "
                          + "to one 255 entry)." + ClearDropText(r, edit)
                    });
                rowHead.Children.Add(rtitle);
                sp.Children.Add(rowHead);

                // Cleared rows: warning naming the outcome routes the collapse drops -
                // red only for routes served nowhere else, amber for ones the clear kept
                // as their own chance row (mixup sync). Repaints whenever the surviving
                // always (255) target changes.
                bool cleared = edit.ClearCondition;
                TextBlock? clearWarn = null;
                void PaintClearWarning()
                {
                    if (clearWarn is null) return;
                    if (!cleared) { clearWarn.Visibility = Visibility.Collapsed; return; }
                    var (keep, lost, kept) = ClassifyClearDrops(r, edit);
                    if (lost.Count == 0 && kept.Count == 0) { clearWarn.Visibility = Visibility.Collapsed; return; }
                    var parts = new List<string>();
                    if (lost.Count > 0) parts.Add($"drops {string.Join(", ", lost)}");
                    if (kept.Count > 0) parts.Add($"keeps {string.Join(", ", kept)} as chance row(s)");
                    string keepName = edit.RetargetNodeIds.TryGetValue(255, out var keepNode)
                        ? EdgeNodeName(keepNode)
                        : keep >= 0 ? StructDisplay(keep) : "?";
                    clearWarn.Foreground = lost.Count > 0 ? RedBrush : WarnBrush;
                    clearWarn.Text = $"Clearing {string.Join("; ", parts)} - the row would instead route "
                        + $"unconditionally → {keepName}. Change which target "
                        + "survives on the always (255) line below.";
                    clearWarn.Visibility = Visibility.Visible;
                }
                clearWarn = new TextBlock
                {
                    Foreground = RedBrush,
                    FontSize = 10,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(4, 6, 4, 0),
                    Visibility = Visibility.Collapsed
                };
                sp.Children.Add(clearWarn);
                PaintClearWarning();

                if (r.RowKeys.Count > 0 || cleared)
                {
                    sp.Children.Add(new TextBlock
                    {
                        Text = "Condition result → target",
                        Foreground = DimBrush,
                        FontWeight = FontWeights.Bold,
                        FontSize = 10,
                        Margin = new Thickness(4, 6, 0, 2)
                    });
                    // Synthetic always (255) line when the vanilla map has no 255 key:
                    // after the clear this is the row's only route - show it (seeded
                    // with the collapse target) instead of hiding the choice in export.
                    if (cleared && !r.RowKeys.Contains((byte)255))
                    {
                        int seed = CollapseSeedTarget(r);
                        int initT = edit.Retarget.TryGetValue(255, out var sv) ? sv : seed;
                        int initNodeId = edit.RetargetNodeIds.TryGetValue(255, out var inv255) ? inv255 : -1;
                        if (initNodeId >= 0) initT = -1;
                        var cell = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
                        cell.Children.Add(new TextBlock
                        {
                            Text = OutcomeLabel(255, r.RowKeys, pending),
                            Foreground = DimBrush,
                            FontSize = 10,
                            VerticalAlignment = VerticalAlignment.Center,
                            Margin = new Thickness(4, 0, 4, 0),
                            MinWidth = 86,
                            ToolTip = "The row's unconditional route once the condition clears (key 255) - the "
                                + "single entry export keeps when it collapses the outcome map. Pick which target "
                                + "survives; the red note above lists what that drops."
                        });
                        cell.Children.Add(BuildOutcomeCombo(r, 255, seed, initT, initT != seed || initNodeId >= 0, choices,
                            canEdit, "unconditional default", () => { SyncMixupRows(); Rebuild(); },
                            initNodeId, canEdit ? new Action(() => RequestPick(r, 255)) : null));
                        sp.Children.Add(cell);
                    }
                    for (int ki = 0; ki < r.RowKeys.Count; ki++)
                    {
                        byte key = r.RowKeys[ki];
                        int vanilla = ki < r.RowVanillaTargets.Count ? r.RowVanillaTargets[ki] : -1;
                        int currentT = vanilla;
                        int currentNodeId = -1;
                        bool editedT = false;
                        if (cleared && key == 255)
                        {
                            // The survivor: show the seeded/chosen collapse target (the
                            // export fallback when no 255 retarget exists is outcome 0,
                            // which need not equal this key's vanilla value).
                            if (edit.RetargetNodeIds.TryGetValue(255, out var n255))
                            {
                                currentNodeId = n255;
                                currentT = -1;
                                editedT = true;
                            }
                            else
                            {
                                currentT = edit.Retarget.TryGetValue(255, out var rt255)
                                    ? rt255
                                    : CollapseSeedTarget(r);
                                editedT = currentT != vanilla;
                            }
                        }
                        else if (!edit.ClearCondition && edit.RetargetNodeIds.TryGetValue(key, out var nkey))
                        {
                            currentNodeId = nkey;
                            currentT = -1;
                            editedT = true;
                        }
                        else if (!edit.ClearCondition && edit.Retarget.TryGetValue(key, out var rt))
                        {
                            currentT = rt;
                            // Seeded values (edge natural target, added-condition keys at
                            // the row's own target) are not user edits until they differ.
                            editedT = rt != vanilla
                                && (r.Edge == null || rt != TreeRowConditionEdit.TargetNatural);
                        }

                        bool dropped = cleared && key != 255;
                        var cell = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
                        cell.Children.Add(new TextBlock
                        {
                            Text = OutcomeLabel(key, r.RowKeys, pending),
                            Foreground = dropped ? RedBrush : (key == 255 ? DimBrush : BlueBrush),
                            TextDecorations = dropped ? TextDecorations.Strikethrough : null,
                            FontSize = 10,
                            VerticalAlignment = VerticalAlignment.Center,
                            Margin = new Thickness(4, 0, 4, 0),
                            MinWidth = 86,
                            ToolTip = OutcomeTip(key, r.RowKeys, pending)
                                + (dropped ? " Dropped when the condition clears (see the red note above)." : "")
                        });
                        bool keyEnabled = canEdit && (!cleared || key == 255);
                        cell.Children.Add(BuildOutcomeCombo(r, key, vanilla, currentT, editedT, choices,
                            keyEnabled, null,
                            key == 255
                                ? (Action?)(() =>
                                {
                                    // The survivor pick decides which outcomes the clear
                                    // drops - re-run the mixup sync when it is the clear's
                                    // own row; plain retargets only repaint the warning.
                                    if (cleared) { SyncMixupRows(); Rebuild(); }
                                    else PaintClearWarning();
                                })
                                : null,
                            currentNodeId,
                            keyEnabled ? new Action(() => RequestPick(r, key)) : null));
                        sp.Children.Add(cell);
                    }
                    // Merged links: keys export adds to this row's m_TargetNodes so the
                    // link rides the row's condition instead of its own chance row.
                    foreach (var ak in edit.AddedKeys.OrderBy(kv => kv.Key).ToList())
                    {
                        byte mkey = ak.Key;
                        string mtarget = EdgeNodeName(ak.Value);
                        var mcell = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
                        mcell.Children.Add(new TextBlock
                        {
                            Text = OutcomeLabel(mkey, r.RowKeys, pending),
                            Foreground = GreenBrush,
                            FontSize = 10,
                            VerticalAlignment = VerticalAlignment.Center,
                            Margin = new Thickness(4, 0, 4, 0),
                            MinWidth = 86,
                            ToolTip = $"Merged link: when the condition returns {mkey}, route to {mtarget}. "
                                + "Written into this row's m_TargetNodes on export - the link gets no "
                                + "separate chance row and no second condition instance."
                        });
                        mcell.Children.Add(new TextBlock
                        {
                            Text = $"→ {mtarget}",
                            Foreground = GreenBrush,
                            FontSize = 10,
                            VerticalAlignment = VerticalAlignment.Center,
                            Margin = new Thickness(0, 0, 6, 0),
                            TextTrimming = TextTrimming.CharacterEllipsis,
                            MaxWidth = 260
                        });
                        var unlinkBtn = new Button
                        {
                            Content = "unlink",
                            Style = (Style)FindResource("DarkButton"),
                            FontSize = 9,
                            Padding = new Thickness(6, 1, 6, 1),
                            IsEnabled = canEdit,
                            ToolTip = "Return this link to its own appended chance row "
                                + "(drops the merged outcome key from this row)."
                        };
                        unlinkBtn.Click += (_, _) => UnmergeKey(r, mkey);
                        mcell.Children.Add(unlinkBtn);
                        sp.Children.Add(mcell);
                    }
                }

                sub.Child = sp;
                bp.Children.Add(sub);
            }

            block.Child = bp;
            content.Children.Add(block);
        }

        if (anyStale)
            content.Children.Add(new TextBlock
            {
                Text = "Some rows show text-only params: this save predates structured condition data - "
                    + "re-parse the unit (Reset/reload the project) to edit those values.",
                Foreground = WarnBrush,
                FontSize = 10,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(4, 6, 0, 0)
            });

        secs.Add(BuildSectionCard(
            $"Condition edits · {condRows.Count} row{(condRows.Count == 1 ? "" : "s")} · {groups} condition{(groups == 1 ? "" : "s")}",
            "Grouped by condition instance (params are shared) - outcome routing written with the tree on export.",
            content,
            viewOnly: !canEdit));
        return secs;
    }

    /// <summary>Pickable condition for an unconditional row: an existing same-file
    /// instance (ShareExportIndex - params shared by definition) or a cross-file clone
    /// source (CloneRel/CloneExportIndex - appended by the exporter).</summary>
    private sealed class CondChoice
    {
        public string Label = "";
        public int ShareExportIndex;
        public string? CloneRel;
        public int CloneExportIndex = -1;
        public string ClassName = "";
        public List<ConditionParamInfo>? Params;
        // Example row's m_TargetNodes keys - the outcome keyset an edge row seeds from.
        public List<byte> ExampleKeys = new();
        // >= 0 on "merge into Row N" choices: the link joins that row's m_TargetNodes
        // as a new outcome key instead of becoming its own condition row.
        public int MergeTargetRow = -1;
        public override string ToString() => Label;
    }

    /// <summary>Card listing every vanilla-unconditional row with a condition picker:
    /// same-file instances first (share), then scanned catalog entries (clone at
    /// export). Empty without editable unconditional rows.</summary>
    private List<UIElement> BuildAddConditionSections()
    {
        var secs = new List<UIElement>();
        if (!_canEditChances || _rows.Count == 0) return secs;

        var choices = CollectSameFileChoices();
        if (_condCatalog != null)
        {
            foreach (var ci in _condCatalog)
            {
                choices.Add(new CondChoice
                {
                    Label = $"{ci.ClassName} · from {System.IO.Path.GetFileName(ci.SourceComboRel)}",
                    CloneRel = ci.SourceComboRel,
                    CloneExportIndex = ci.SourceExportIndex,
                    ClassName = ci.ClassName,
                    Params = ci.Params,
                    ExampleKeys = ci.ExampleKeys
                });
            }
        }
        choices.Insert(0, new CondChoice { Label = "— choose condition —" });
        bool catalogEmpty = choices.Count == 1;
        // "No condition" only on rows that currently show one: a vanilla-conditioned row
        // empties its instance ref on export (ClearCondition - the vanilla "instance ref
        // empty" state), an added/new-link row drops its edit and returns to
        // unconditional, and an already-empty-ref row reverts to its vanilla state -
        // the same actions as the grouped card's buttons.
        CondChoice? noCond = null;
        if (_rows.Any(r => r.ConditionPresent))
        {
            noCond = new CondChoice { Label = "— no condition —" };
            choices.Insert(1, noCond);
        }

        var content = new StackPanel { Margin = new Thickness(10, 4, 10, 8) };
        content.Children.Add(new TextBlock
        {
            Text = "Share uses a condition instance that already exists in this file (parameters shared with "
                + "every referencing row). Clones copy a condition from another combo file when this project "
                + "exports. Rows that already have one can switch to another class directly - routing shows in "
                + "the group card below.",
            Foreground = DimBrush,
            FontSize = 10,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(4, 0, 4, 6)
        });
        if (catalogEmpty)
            content.Children.Add(new TextBlock
            {
                Text = "No condition instances known yet - scanning combo files automatically, "
                    + "or click the scan button below.",
                Foreground = WarnBrush,
                FontSize = 10,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(4, 0, 4, 4)
            });

        foreach (var r in _rows)
        {
            var line = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
            var lbl = new TextBlock
            {
                Text = $"{RowTitle(r)} · {r.Effective:0.#}% → {r.TargetSummary}",
                Foreground = r.ConditionPresent ? GreenBrush : TextBrush,
                FontSize = 10,
                VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = 260,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(4, 0, 8, 0),
                ToolTip = r.Edge != null
                    ? $"New link's transition row (appended on export). Target: {r.TargetSummary}"
                    : r.ConditionPresent
                        ? r.ConditionInstanceIndex == 0
                            ? $"Instance ref empty - no condition, routes unconditionally. "
                              + $"Pick a condition to add one. Target: {r.TargetSummary}"
                            : $"Has {r.ConditionClass} - pick another class to switch, or Remove in the group card below."
                        : $"Transition row currently unconditional. Target: {r.TargetSummary}"
            };
            DockPanel.SetDock(lbl, Dock.Left);
            var rowChoices = new List<CondChoice>(choices);
            if (!r.ConditionPresent && noCond != null) rowChoices.RemoveAll(c => ReferenceEquals(c, noCond));
            // New links can join an existing conditioned row instead of getting their
            // own condition row - one choice per eligible target row (graph links only:
            // staged mixup links merge nowhere).
            if (r.Edge != null && _graph.Edges.Contains(r.Edge))
            {
                foreach (var t in MergeTargets())
                    rowChoices.Add(new CondChoice
                    {
                        Label = $"↳ merge into Row {t.RowIndex + 1} · {t.ConditionClass}",
                        MergeTargetRow = t.RowIndex,
                        ClassName = t.ConditionClass
                    });
            }
            var combo = new ComboBox
            {
                IsEditable = false,
                Width = 320,
                FontSize = 10,
                Margin = new Thickness(0, 0, 4, 0),
                ItemsSource = rowChoices,
                ToolTip = "Pick a condition class for this row - same-file entries share an existing instance, "
                    + "entries from other files clone at export. Routing appears in the group card below after picking."
            };
            // Preselect the row's current class so conditioned rows show (and can switch)
            // without a Remove-first dance; unmatched classes get a transient label so the
            // combo never renders blank on a conditioned row. Vanilla "instance ref
            // empty" rows preselect "- no condition -" - they already are that state
            // (the placeholder would otherwise win the ClassName "" match).
            CondChoice? current = null;
            if (r.ConditionPresent)
            {
                current = r.ConditionInstanceIndex == 0 && noCond != null
                    ? noCond
                    : rowChoices.FirstOrDefault(c =>
                            c.MergeTargetRow < 0 && c.ShareExportIndex != 0
                            && c.ShareExportIndex == r.ConditionInstanceIndex)
                        ?? rowChoices.FirstOrDefault(c =>
                            c.MergeTargetRow < 0 && c.ClassName == r.ConditionClass);
                if (current == null)
                {
                    current = new CondChoice { Label = $"{r.ConditionClass} (current)", ClassName = r.ConditionClass };
                    rowChoices.Add(current);
                }
            }
            _syncing = true;
            combo.SelectedItem = current ?? rowChoices[0];
            _syncing = false;
            var targetRow = r;
            combo.SelectionChanged += (_, _) =>
            {
                if (_syncing) return;
                if (combo.SelectedItem is CondChoice ch)
                {
                    if (ch.MergeTargetRow >= 0)
                    {
                        var target = _rows.FirstOrDefault(x => x.Edge == null
                            && x.RowIndex == ch.MergeTargetRow);
                        if (target != null) MergeEdgeIntoRow(targetRow: target, edgeRow: targetRow);
                    }
                    else if (ReferenceEquals(ch, noCond)) RemoveCondition(targetRow);
                    else ApplyConditionChoice(targetRow, ch);
                }
            };
            line.Children.Add(lbl);
            line.Children.Add(combo);
            content.Children.Add(line);
        }

        if (!string.IsNullOrEmpty(_contentDir))
        {
            var scanRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            var scanBtn = new Button
            {
                Content = _condCatalog == null
                    ? "Scan other combo files for condition classes..."
                    : $"Rescan condition classes ({_condCatalog.Count} found)",
                Style = (Style)FindResource("DarkButton"),
                FontSize = 10,
                Padding = new Thickness(8, 3, 8, 3)
            };
            var scanStatus = new TextBlock
            {
                Foreground = DimBrush,
                FontSize = 10,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Text = _catalogScanning ? "Scanning all combo files..." : ""
            };
            bool scanning = false;
            scanBtn.Click += async (_, _) =>
            {
                if (scanning || _catalogScanning || string.IsNullOrEmpty(_contentDir)) return;
                scanning = true;
                _catalogScanning = true;
                scanBtn.IsEnabled = false;
                scanStatus.Text = "Scanning combo files...";
                try
                {
                    string dir = _contentDir!;
                    var found = await System.Threading.Tasks.Task.Run(() =>
                        ConditionCatalog.Scan(dir, msg => Dispatcher.Invoke(() => scanStatus.Text = msg)));
                    _condCatalog = found;
                    _catalogScanning = false;
                    Rebuild();
                }
                catch (Exception ex)
                {
                    ErrorLog.Write("COND-CATALOG", ex);
                    scanStatus.Text = "scan failed - see error log";
                    scanning = false;
                    _catalogScanning = false;
                    scanBtn.IsEnabled = true;
                }
            };
            scanRow.Children.Add(scanBtn);
            scanRow.Children.Add(scanStatus);
            content.Children.Add(scanRow);
        }

        secs.Add(BuildSectionCard(
            $"Add or change a condition · {_rows.Count} row{(_rows.Count == 1 ? "" : "s")}",
            "Pick a condition class per row - written with the tree on export.",
            content,
            viewOnly: false));
        return secs;
    }

    /// <summary>Distinct same-file condition instances (class + params) gathered from
    /// every parsed struct's rows - the share candidates.</summary>
    private List<CondChoice> CollectSameFileChoices()
    {
        var list = new List<CondChoice>();
        var seen = new HashSet<int>();
        if (_graph.TreeRows != null)
        {
            foreach (var kv in _graph.TreeRows)
            {
                if (kv.Value == null) continue;
                foreach (var tr in kv.Value)
                {
                    if (tr.ConditionInstanceIndex <= 0 || !seen.Add(tr.ConditionInstanceIndex)) continue;
                    list.Add(new CondChoice
                    {
                        Label = $"{tr.ConditionClass} · export {tr.ConditionInstanceIndex} (this file)",
                        ShareExportIndex = tr.ConditionInstanceIndex,
                        ClassName = tr.ConditionClass,
                        Params = tr.ConditionParamList,
                        ExampleKeys = new List<byte>(tr.Keys)
                    });
                }
            }
        }
        list.Sort((a, b) => string.Compare(a.ClassName, b.ClassName, StringComparison.OrdinalIgnoreCase));
        return list;
    }

    /// <summary>Attach the chosen condition to a row: seeds the row edit (share index or
    /// clone source), flips the row into the grouped condition card, and rebuilds.</summary>
    private void ApplyConditionChoice(ChanceRow row, CondChoice choice)
    {
        // Placeholder "- choose condition -" / transient "(current)" label - not a source.
        if (choice.ShareExportIndex == 0 && string.IsNullOrEmpty(choice.CloneRel)) return;
        var e = EditFor(row);
        // A re-pick after a Clear must win: the export row pass runs Clear first.
        bool wasCleared = e.ClearCondition;
        e.ClearCondition = false;
        if (wasCleared) DropSeededCollapse(e);
        e.SetInstance = choice.ShareExportIndex;
        e.CloneSourceRel = choice.CloneRel;
        e.CloneSourceExport = choice.CloneExportIndex;
        e.CloneClass = choice.ClassName;
        bool wasPresent = row.ConditionPresent;
        row.ConditionPresent = true;
        row.ConditionClass = choice.ClassName;
        row.ConditionParamList = choice.Params;
        row.ConditionInstanceIndex = choice.ShareExportIndex != 0
            ? choice.ShareExportIndex
            : TreeRowConditionEdit.CloneVirtualKey(choice.CloneRel, choice.CloneExportIndex);
        if (row.Edge != null)
        {
            // The row does not exist yet, so there is no vanilla keyset - seed the
            // example row's outcome keys, each routing to the link's own target until
            // the user re-routes or blocks one.
            var keys = NormalizeExampleKeys(choice.ExampleKeys);
            row.RowKeys = new List<byte>(keys);
            row.RowVanillaTargets = keys.Select(_ => TreeRowConditionEdit.TargetNatural).ToList();
            e.Retarget.Clear();
            e.RetargetNodeIds.Clear();
            foreach (var k in keys) e.Retarget[k] = TreeRowConditionEdit.TargetNatural;
        }
        else if (!wasPresent)
        {
            // Vanilla row gaining its first condition: the map is a single 255 entry -
            // seed the example's outcome keys with every outcome keeping the row's
            // current target, so routing behaves exactly as before until re-routed.
            // Export rewrites the map to this keyed shape (COND REKEY); with no seeded
            // keys (older saves) export falls back to {0,1} at the original target.
            var keys = NormalizeExampleKeys(choice.ExampleKeys);
            row.RowKeys = new List<byte>(keys);
            int origT = row.RowVanillaTargets.Count > 0 ? row.RowVanillaTargets[0] : -1;
            row.RowVanillaTargets = keys.Select(_ => origT).ToList();
            if (origT >= 0)
            {
                e.Retarget.Clear();
                e.RetargetNodeIds.Clear();
                foreach (var k in keys) e.Retarget[k] = origT;
            }
        }
        SyncMixupRows();
        Rebuild();
    }

    /// <summary>The target export's collapse keeps for this row when no explicit 255
    /// retarget exists: outcome 0's target, else the first key's - the same rule
    /// ApplyConditionOverrides uses when it empties the instance ref.</summary>
    private static int CollapseSeedTarget(ChanceRow r)
    {
        int ki = r.RowKeys.IndexOf(0);
        if (ki < 0) ki = 0;
        return ki < r.RowVanillaTargets.Count ? r.RowVanillaTargets[ki] : -1;
    }

    /// <summary>Clear path: seed Retarget[255] with the collapse target so the row's
    /// surviving unconditional route stays visible and editable (export applies
    /// Retarget[255] after collapsing the map to one 255 entry). Other keys' retargets
    /// go - those outcomes no longer exist once the condition is cleared.</summary>
    private static void SeedCollapseTarget(ChanceRow row, TreeRowConditionEdit e)
    {
        e.Retarget.Clear();
        e.RetargetNodeIds.Clear();
        e.SeededCollapse = false;
        int seed = CollapseSeedTarget(row);
        if (seed >= 0)
        {
            e.Retarget[255] = seed;
            e.SeededCollapse = true;
        }
    }

    /// <summary>Restore path: drop the collapse-time route so clear-then-restore never
    /// leaves a retarget the user only ever saw as part of the cleared state.</summary>
    private static void DropSeededCollapse(TreeRowConditionEdit e)
    {
        if (!e.SeededCollapse) return;
        e.Retarget.Remove(255);
        e.RetargetNodeIds.Remove(255);
        e.SeededCollapse = false;
    }

    /// <summary>The row's condition edit as it stands: a local touch wins, else the
    /// graph's saved edit, else null. Does not lazily seed - safe for read-only passes.</summary>
    private TreeRowConditionEdit? EffectiveRowEdit(ChanceRow r)
    {
        if (_condRowEdits.TryGetValue(r.RowIndex, out var local)) return local;
        if (r.RowIndex >= 0 && _structIdx >= 0
            && _graph.TreeRowConditionOverrides.TryGetValue(_structIdx, out var map)
            && map.TryGetValue(r.RowIndex, out var stored))
            return stored;
        return null;
    }

    /// <summary>Post-override target set (tree indices) one row serves after export.
    /// New-link rows: the link's own target node (that row exists for the link).
    /// Vanilla rows: the shared clear/retarget math the export writeback uses.</summary>
    private List<int> RowPostTargets(ChanceRow r)
    {
        if (r.Edge != null)
        {
            var to = _graph.Nodes.FirstOrDefault(n => n.Id == r.Edge!.ToNodeId);
            return to != null && to.TreeIndex >= 0 ? new List<int> { to.TreeIndex } : new List<int>();
        }
        return TreeChanceMath.PostOverrideTargets(r.RowKeys, r.RowVanillaTargets, EffectiveRowEdit(r));
    }

    /// <summary>Split a cleared row's dropped routes. Keep = the surviving unconditional
    /// route. Lost = dropped nowhere else serves. Kept = the clear's own mixup row now
    /// serves it. Routes another row still reaches are not drops at all - omitted.</summary>
    private (int Keep, List<string> Lost, List<string> Kept) ClassifyClearDrops(ChanceRow r, TreeRowConditionEdit e)
    {
        int keep = e.Retarget.TryGetValue(255, out var k) ? k : CollapseSeedTarget(r);
        var plain = new HashSet<int>();
        var managed = new HashSet<int>();
        foreach (var row in _rows)
        {
            if (ReferenceEquals(row, r)) continue;
            bool isManaged = row.Edge != null && row.Edge.FromClearMixup
                && row.Edge.FromNodeId == _node.Id;
            foreach (var t in RowPostTargets(row))
                (isManaged ? managed : plain).Add(t);
        }
        var lost = new List<string>();
        var kept = new List<string>();
        for (int i = 0; i < r.RowKeys.Count; i++)
        {
            byte key = r.RowKeys[i];
            if (key == 255) continue;
            int t = i < r.RowVanillaTargets.Count ? r.RowVanillaTargets[i] : -1;
            if (t < 0 || t == keep) continue;
            if (plain.Contains(t)) continue;   // another row still routes it - not a loss
            string name = $"{OutcomeLabel(key, r.RowKeys).TrimEnd()} {StructDisplay(t)}";
            if (managed.Contains(t)) kept.Add(name);
            else lost.Add(name);
        }
        return (keep, lost, kept);
    }

    /// <summary>Tooltip suffix for the clear state - kept-as-chance-row routes named so
    /// the collapse's trade reads exactly as the warning line does.</summary>
    private string ClearDropText(ChanceRow r, TreeRowConditionEdit e)
    {
        var (_, lost, kept) = ClassifyClearDrops(r, e);
        if (lost.Count == 0 && kept.Count == 0) return "";
        var parts = new List<string>();
        if (lost.Count > 0) parts.Add($"Dropping: {string.Join(", ", lost)}");
        if (kept.Count > 0) parts.Add($"keeping as chance row(s): {string.Join(", ", kept)}");
        return " " + string.Join("; ", parts) + ".";
    }

    /// <summary>Clear/restore sync: keeps this entry's rows in step with the node's
    /// forward links. Forward = non-new links from this node resolving to real tree
    /// indices other than this struct (that target is this entry's own - no row needed;
    /// custom nodes excluded). An outcome the collapse drops but the node still links to
    /// becomes its own chance row - an IsNewLink edge staged with FromClearMixup, its
    /// chance joining at the automatic average (export rebalances). A restore that
    /// re-covers a target drops the mixup row again. Cancel discards all of it.</summary>
    private void SyncMixupRows()
    {
        if (!_canEditChances || _structIdx < 0 || !_vanillaAvailable) return;

        // This node's forward links: treeIndex -> nodeId.
        var forward = new Dictionary<int, int>();
        foreach (var e in _graph.Edges)
        {
            if (e.IsRedirect || e.IsNewLink || e.FromNodeId != _node.Id) continue;
            var to = _graph.Nodes.FirstOrDefault(n => n.Id == e.ToNodeId);
            if (to == null || to.TreeIndex < 0 || to.TreeIndex == _structIdx) continue;
            forward[to.TreeIndex] = e.ToNodeId;
        }

        // Targets every row except this node's own mixup rows already serves.
        var covered = new HashSet<int>();
        foreach (var r in _rows)
        {
            if (r.Edge != null && r.Edge.FromClearMixup && r.Edge.FromNodeId == _node.Id) continue;
            foreach (var t in RowPostTargets(r)) covered.Add(t);
        }

        // Mixup rows still needed: drop the rest (restore re-covered their target).
        var needed = new HashSet<int>(forward.Keys.Where(t => !covered.Contains(t)));
        foreach (var r in _rows
            .Where(r => r.Edge != null && r.Edge.FromClearMixup && r.Edge.FromNodeId == _node.Id)
            .ToList())
        {
            int ti = RowPostTargets(r) is { Count: > 0 } ts ? ts[0] : -1;
            if (ti >= 0 && needed.Contains(ti)) continue;
            _rows.Remove(r);
            if (_graph.Edges.Contains(r.Edge!)) _removedMixupEdges.Add(r.Edge!);
            else _stagedEdges.Remove(r.Edge!);
        }

        // Row per needed target that lacks one. New edges take the cleared row's input
        // (same trigger the outcome used to ride on); a graph edge restored mid-session
        // is un-staged instead of duplicated.
        var have = new HashSet<int>();
        foreach (var r in _rows)
            if (r.Edge != null && r.Edge.FromClearMixup && r.Edge.FromNodeId == _node.Id)
                foreach (var t in RowPostTargets(r)) have.Add(t);
        string mixInput = "";
        foreach (var r in _rows)
        {
            if (r.Edge != null || r.RowIndex < 0) continue;
            if (EffectiveRowEdit(r)?.ClearCondition == true) { mixInput = r.InputName; break; }
        }
        foreach (var (ti, nodeId) in forward)
        {
            if (!needed.Contains(ti) || have.Contains(ti)) continue;
            var existing = _graph.Edges.FirstOrDefault(e => e.FromClearMixup
                && e.FromNodeId == _node.Id && e.ToNodeId == nodeId);
            ComboEdge edge;
            if (existing != null)
            {
                edge = existing;
                _removedMixupEdges.Remove(existing);
            }
            else
            {
                edge = new ComboEdge
                {
                    FromNodeId = _node.Id,
                    ToNodeId = nodeId,
                    InputName = mixInput,
                    IsNewLink = true,
                    FromClearMixup = true,
                    NewLinkProbability = null
                };
                _stagedEdges.Add(edge);
            }
            _rows.Add(new ChanceRow
            {
                RowIndex = -1,
                InputName = edge.InputName,
                TargetSummary = EdgeNodeName(nodeId),
                Original = 0f,
                Explicit = edge.NewLinkProbability,
                IsNewLink = true,
                Edge = edge
            });
            have.Add(ti);
        }
        // Effective values for rows the sync just added or dropped (new rows join the
        // automatic split; removals rebalance the display back to the plain set).
        Recompute();
    }

    /// <summary>Dropdown "- no condition -": an added/new-link row drops its edit and
    /// returns to unconditional display; a vanilla row whose instance ref is already
    /// empty just drops any pending condition back to that vanilla state (which is
    /// already "no condition") while keeping its routing edits; any other vanilla row
    /// flags ClearCondition instead (export empties the instance ref and collapses the
    /// map, seeding Retarget[255] so the surviving route stays editable) - the same
    /// actions the grouped card's Remove/Clear buttons perform.</summary>
    private void RemoveCondition(ChanceRow row)
    {
        if (!row.ConditionPresent) return;
        if (!row.VanillaHadCondition)
        {
            DropAddedCondition(row);
        }
        else if (row.VanillaInstanceEmpty)
        {
            // Vanilla already is "instance ref empty": drop any pending condition -
            // and any stale ClearCondition flag - back to that state. Retarget is
            // kept: routing edits are legitimate on an empty-ref row.
            var e = EditFor(row);
            bool wasCleared = e.ClearCondition;
            e.ClearCondition = false;
            if (wasCleared) DropSeededCollapse(e);
            e.SetInstance = 0;
            e.CloneSourceRel = null;
            e.CloneSourceExport = -1;
            e.CloneClass = "";
            row.ConditionPresent = true;
            row.ConditionInstanceIndex = 0;
            row.ConditionClass = "";
            row.ConditionParamList = null;
        }
        else
        {
            var e = EditFor(row);
            DropMergesInto(row);   // cleared rows route unconditionally - merges die with it
            e.ClearCondition = true;
            SeedCollapseTarget(row, e);
        }
        SyncMixupRows();
        Rebuild();
    }

    /// <summary>Row edit fully reverted: instance/clone fields and routing cleared, row
    /// display back to unconditioned - BuildRowConditions then drops it from the commit
    /// (a row with nothing pending reverts to vanilla at export).</summary>
    private void DropAddedCondition(ChanceRow row)
    {
        DropMergesInto(row);
        var e = EditFor(row);
        e.ClearCondition = false;
        e.Retarget.Clear();
        e.RetargetNodeIds.Clear();
        e.SeededCollapse = false;
        e.SetInstance = 0;
        e.CloneSourceRel = null;
        e.CloneSourceExport = -1;
        e.CloneClass = "";
        row.ConditionPresent = false;
        row.ConditionInstanceIndex = 0;
        row.ConditionClass = "";
        row.ConditionParamList = null;
    }

    /// <summary>⌖ on an outcome combo: freeze the dialog's working state and close like
    /// Cancel so MainWindow can run pick-on-graph; a fresh instance resumes from
    /// <see cref="PickSuspend"/> after the pick applies.</summary>
    private void RequestPick(ChanceRow row, byte key)
    {
        PickSuspend = new PickSuspendState
        {
            VanillaAvailable = _vanillaAvailable,
            Rows = new List<ChanceRow>(_rows),
            RowEdits = new Dictionary<int, TreeRowConditionEdit>(_condRowEdits),
            EdgeEdits = new Dictionary<string, TreeRowConditionEdit>(_edgeCondEdits),
            CondParamEdits = _condParamEdits.ToDictionary(kv => kv.Key,
                kv => new Dictionary<string, string>(kv.Value)),
            StagedEdges = new List<ComboEdge>(_stagedEdges),
            RemovedMixupEdges = new List<ComboEdge>(_removedMixupEdges),
            StagedMerges = new Dictionary<ComboEdge, (int rowIdx, int key)?>(_stagedMerges),
            Row = row,
            Key = key
        };
        SuspendedForPick = true;
        DialogResult = false;
    }

    /// <summary>Routes the picked row's outcome key to the picked node: user-authored
    /// targets ride RetargetNodeIds (graph id, resolved at export); vanilla picks go
    /// through Retarget as a tree index. The two stay mutually exclusive per key.</summary>
    internal static void ApplyPickedTarget(PickSuspendState state, ComboNode target)
    {
        var row = state.Row;
        TreeRowConditionEdit? e = null;
        if (row.Edge != null)
        {
            string edgeKey = ComboGraph.EdgeConditionKey(row.Edge);
            if (!state.EdgeEdits.TryGetValue(edgeKey, out e))
            {
                e = new TreeRowConditionEdit();
                state.EdgeEdits[edgeKey] = e;
            }
        }
        else if (!state.RowEdits.TryGetValue(row.RowIndex, out e))
        {
            e = new TreeRowConditionEdit();
            state.RowEdits[row.RowIndex] = e;
        }
        if (e == null) return;
        if (target.TreeIndex >= 0)
        {
            e.Retarget[state.Key] = target.TreeIndex;
            e.RetargetNodeIds.Remove(state.Key);
        }
        else
        {
            e.RetargetNodeIds[state.Key] = target.Id;
            e.Retarget.Remove(state.Key);
        }
    }

    /// <summary>Picker item for one outcome target (ToString = editable-combo text).
    /// NodeId >= 0 marks a user-authored target (graph id, resolved at export through
    /// RetargetNodeIds); vanilla choices keep TreeIndex and NodeId -1.</summary>
    private sealed class NodeChoice
    {
        public string Label = "";
        public int TreeIndex = -1;
        public int NodeId = -1;
        public override string ToString() => Label;
    }

    /// <summary>Candidate targets for a row's outcomes: connected outgoing nodes first
    /// (→ prefix), then every other node. Excludes root/removed/self. Vanilla rows list
    /// tree-indexed nodes (including action/event/redirect - vanilla routes condition
    /// outcomes to dash/action nodes too); user-authored nodes follow, labeled [new]
    /// (their export index only exists after export, so they carry their graph id).</summary>
    private List<NodeChoice> OutcomeChoices(ChanceRow r)
    {
        var outgoing = new HashSet<int>(_graph.Edges
            .Where(e => e.FromNodeId == _node.Id).Select(e => e.ToNodeId));
        var seen = new HashSet<int>();
        var connected = new List<NodeChoice>();
        var others = new List<NodeChoice>();
        var userConnected = new List<NodeChoice>();
        var userOthers = new List<NodeChoice>();
        foreach (var n in _graph.Nodes)
        {
            if (n.IsRoot || n.Removed) continue;
            string kind = n.IsActionNode ? " · action" : n.IsEventNode ? " · event" : n.IsRedirect ? " · redirect" : "";
            if (n.TreeIndex < 0)
            {
                if (n.Id == _node.Id) continue;
                var uc = new NodeChoice { NodeId = n.Id, Label = $"[new] {NodeDisplay(n)}{kind}" };
                if (outgoing.Contains(n.Id)) userConnected.Add(uc);
                else userOthers.Add(uc);
                continue;
            }
            if (_node.TreeIndex >= 0 && n.TreeIndex == _node.TreeIndex) continue;
            if (!seen.Add(n.TreeIndex)) continue;
            var choice = new NodeChoice { TreeIndex = n.TreeIndex, Label = $"{NodeDisplay(n)} (tree {n.TreeIndex}){kind}" };
            if (outgoing.Contains(n.Id)) connected.Add(choice);
            else others.Add(choice);
        }
        connected.Sort((a, b) => a.TreeIndex.CompareTo(b.TreeIndex));
        others.Sort((a, b) => a.TreeIndex.CompareTo(b.TreeIndex));
        userConnected.Sort((a, b) => a.NodeId.CompareTo(b.NodeId));
        userOthers.Sort((a, b) => a.NodeId.CompareTo(b.NodeId));
        foreach (var c in connected) c.Label = "→ " + c.Label;
        foreach (var c in userConnected) c.Label = "→ " + c.Label;
        connected.AddRange(others);
        connected.AddRange(userConnected);
        connected.AddRange(userOthers);
        return connected;
    }

    /// <summary>Outcome chip text: True/False only for pure boolean key sets {0}/{0,1}
    /// (polarity verified in-game 2026-10-12: key 0 = True, key 1 = False - UE branch
    /// pin order); anything else stays honest. Added-condition rows label their lone
    /// 255 as "default" - the engine's fall-through for a condition that matches
    /// nothing is untested.</summary>
    private static string OutcomeLabel(byte key, List<byte> keys, bool pending = false)
    {
        if (key == 255) return pending ? "default →" : "always →";
        bool pair = keys.Contains(0) && keys.Contains(1) && keys.All(k => k <= 1);
        if (pair && key == 0) return "True (0) →";
        if (pair && key == 1) return "False (1) →";
        return $"Returns {key} →";
    }

    private static string OutcomeTip(byte key, List<byte> keys, bool pending = false)
    {
        if (key == 255)
            return pending
                ? "The row's vanilla unconditional routing (key 255). For a newly added condition the engine's "
                  + "fall-through when nothing matches is untested - verify in game."
                : "Unconditional routing (taken when no condition outcome matches).";
        bool pair = keys.Contains(0) && keys.Contains(1) && keys.All(k => k <= 1);
        if (pair && (key == 0 || key == 1))
            return $"Condition returns {key} - shown as {(key == 0 ? "True" : "False")} "
                + "(branch pin order 0 = True, 1 = False - verified in game 2026-10-12). Target node picked on the right.";
        return $"Target when the condition returns {key}. Pick a node or type a tree index.";
    }

    /// <summary>Annotation for an outcome target that points at a stripped conduit: the
    /// raw struct survives in the asset (export keeps the target verbatim), so show where
    /// it actually routes instead of a dangling tree index. Falls back for old saves
    /// whose cache has no conduit map.</summary>
    private string ConduitNote(int treeIndex, string fallback)
        => _graph?.ConduitTargets != null
            && _graph.ConduitTargets.TryGetValue(treeIndex, out var ts)
            && ts.Count > 0
            ? " (conduit → " + string.Join(", ", ts.Select(t => $"tree[{t}]")) + ")"
            : fallback;

    private string ConduitTip(int treeIndex)
        => _graph?.ConduitTargets != null
            && _graph.ConduitTargets.TryGetValue(treeIndex, out var ts)
            && ts.Count > 0
            ? $" Target tree[{treeIndex}] is a stripped conduit - the file routes it to "
              + string.Join(", ", ts.Select(t => $"tree[{t}]")) + "; export keeps the conduit target."
            : "";

    /// <summary>Editable outcome picker: choose a candidate (connected → first) or type a
    /// raw tree index; commits into this row's Retarget like the old number box.
    /// defaultLabelOverride names the reference for rows with no vanilla key of their
    /// own (the synthetic always/255 line on a cleared row); onCommit repaints the
    /// clear-warning text after a pick (the surviving route decides what is dropped).</summary>
    private UIElement BuildOutcomeCombo(ChanceRow r, byte key, int vanilla, int currentT, bool editedT,
        List<NodeChoice> choices, bool enabled, string? defaultLabelOverride = null, Action? onCommit = null,
        int currentNodeId = -1, Action? onPick = null)
    {
        var list = new List<NodeChoice>(choices);
        bool isEdge = r.Edge != null;
        if (isEdge)
        {
            list.Insert(0, new NodeChoice
            {
                TreeIndex = TreeRowConditionEdit.TargetNatural,
                Label = "link target (default)"
            });
            list.Insert(0, new NodeChoice
            {
                TreeIndex = TreeRowConditionEdit.TargetBlocked,
                Label = "(blocked - never routes)"
            });
        }
        if (currentT >= 0 && list.All(c => c.TreeIndex != currentT))
            list.Insert(0, new NodeChoice
            {
                TreeIndex = currentT,
                Label = $"tree[{currentT}]{ConduitNote(currentT, " (custom)")}"
            });
        if (currentNodeId >= 0 && list.All(c => c.NodeId != currentNodeId))
            list.Insert(0, NewNodeChoiceLabel(currentNodeId));
        string defaultLabel = defaultLabelOverride ?? (isEdge
            ? vanilla == TreeRowConditionEdit.TargetBlocked ? "blocked"
                : vanilla == TreeRowConditionEdit.TargetNatural ? "link target"
                : $"tree[{vanilla}]{ConduitNote(vanilla, "")}"
            : $"vanilla: {vanilla}{ConduitNote(vanilla, "")}");
        var combo = new ComboBox
        {
            IsEditable = true,
            IsEnabled = enabled,
            Width = 250,
            FontSize = 10,
            Margin = new Thickness(4, 0, 0, 0),
            ItemsSource = list,
            ToolTip = (editedT
                ? $"Re-routed ({defaultLabel}). Connected nodes first (→); action/event nodes included; "
                  + "or type a raw tree index."
                : isEdge
                    ? "Pick a connected node (→ prefix), any other node, or block the outcome so the branch "
                      + "never routes on it. Type a raw tree index for anything else."
                    : "Pick a connected node (→ prefix), any other node, or type a tree index. Action/event nodes "
                      + "are valid targets; user-authored nodes (labeled [new]) resolve their index at export.")
                + ConduitTip(currentT) + ConduitTip(vanilla)
        };
        if (editedT) combo.BorderBrush = WarnBrush;
        var sel = currentNodeId >= 0
            ? list.FirstOrDefault(c => c.NodeId == currentNodeId)
            : list.FirstOrDefault(c => c.TreeIndex == currentT);
        _syncing = true;
        combo.SelectedItem = sel;
        combo.Text = sel?.Label ?? (currentT >= 0 ? currentT.ToString() : "");
        _syncing = false;

        var nameTb = new TextBlock
        {
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(5, 0, 0, 0),
            MaxWidth = 140,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        void PaintName(int v, int nodeId = -1)
        {
            if (nodeId >= 0)
            {
                var un = _graph.Nodes.FirstOrDefault(n => n.Id == nodeId);
                nameTb.Text = un != null ? NodeDisplay(un) : $"node #{nodeId}";
                nameTb.Foreground = un != null ? GrayBrush : RedBrush;
                return;
            }
            if (v == TreeRowConditionEdit.TargetBlocked)
            {
                nameTb.Text = "blocked";
                nameTb.Foreground = RedBrush;
                return;
            }
            if (v == TreeRowConditionEdit.TargetNatural && r.Edge != null)
            {
                nameTb.Text = EdgeNodeName(r.Edge.ToNodeId);
                nameTb.Foreground = GrayBrush;
                return;
            }
            var nn = v >= 0 ? _graph.Nodes.FirstOrDefault(n => n.TreeIndex == v) : null;
            nameTb.Text = nn != null ? NodeDisplay(nn) : (v >= 0 ? $"tree[{v}]" : "?");
            nameTb.Foreground = nn != null ? GrayBrush : RedBrush;
        }
        PaintName(currentT, currentNodeId);

        void Commit(int v)
        {
            var ed = EditFor(r);
            ed.RetargetNodeIds.Remove(key);
            // Reverting to the vanilla value drops the edit only on rows whose map was
            // keyed in vanilla; rows that gained a condition keep every key stored so
            // the export re-key never loses an outcome (an absent key would vanish
            // from the rebuilt map). A cleared row always stores: its export fallback
            // (collapse to outcome 0) is not necessarily the 255 chip's vanilla value,
            // so dropping the key would silently diverge from what the box displays.
            bool revertRemoves = !isEdge && r.VanillaHadCondition && v == vanilla && !ed.ClearCondition;
            if (revertRemoves)
            {
                ed.Retarget.Remove(key);
                combo.BorderBrush = BorderBrushInput;
            }
            else
            {
                ed.Retarget[key] = v;
                combo.BorderBrush = v == vanilla ? BorderBrushInput : WarnBrush;
            }
            PaintName(v);
            RefreshGlobalStatus();
            onCommit?.Invoke();
        }

        void CommitNode(int nodeId)
        {
            var ed = EditFor(r);
            ed.RetargetNodeIds[key] = nodeId;
            ed.Retarget.Remove(key);
            combo.BorderBrush = WarnBrush;
            PaintName(-1, nodeId);
            RefreshGlobalStatus();
            onCommit?.Invoke();
        }

        combo.SelectionChanged += (_, _) =>
        {
            if (_syncing) return;
            if (combo.SelectedItem is NodeChoice nc)
            {
                _syncing = true;
                combo.Text = nc.Label;
                _syncing = false;
                if (nc.NodeId >= 0) CommitNode(nc.NodeId);
                else Commit(nc.TreeIndex);
            }
        };
        void OnComboText(string raw)
        {
            if (_syncing) return;
            string t = raw?.Trim() ?? "";
            if (t.Length == 0) { combo.BorderBrush = RedBrush; return; }
            if (combo.SelectedItem is NodeChoice nc && t == nc.Label) return;
            if (int.TryParse(t, out var v) && v >= 0) { Commit(v); return; }
            combo.BorderBrush = RedBrush;
        }
        combo.Loaded += (_, _) =>
        {
            if (combo.Template?.FindName("PART_EditableTextBox", combo) is TextBox editable)
                editable.TextChanged += (_, te) => OnComboText(editable.Text);
        };

        var cell = new StackPanel { Orientation = Orientation.Horizontal };
        cell.Children.Add(combo);
        cell.Children.Add(nameTb);
        if (onPick != null)
        {
            var pickBtn = new Button
            {
                Content = "⌖",
                Width = 22,
                Height = 20,
                Margin = new Thickness(4, 0, 0, 0),
                FontSize = 10,
                Padding = new Thickness(0),
                ToolTip = "Pick target on graph - click a node on the canvas, then confirm. "
                    + "Escape (or \"Pick another\") returns here with every edit intact."
            };
            pickBtn.Click += (_, _) => onPick();
            cell.Children.Add(pickBtn);
        }
        return cell;
    }

    /// <summary>Picker entry for a user-authored target node (graph id), with the
    /// deleted-node fallback when the id no longer resolves.</summary>
    private NodeChoice NewNodeChoiceLabel(int nodeId)
    {
        var n = _graph.Nodes.FirstOrDefault(x => x.Id == nodeId);
        string kind = n == null ? "" : n.IsActionNode ? " · action" : n.IsEventNode ? " · event" : n.IsRedirect ? " · redirect" : "";
        return new NodeChoice
        {
            NodeId = nodeId,
            Label = n != null ? $"[new] {NodeDisplay(n)}{kind}" : $"node #{nodeId} (deleted)"
        };
    }

    /// <summary>View-only cards for the node's own m_ConditionalAttacks and
    /// m_RequiredTags (editing those lands in a later phase).</summary>
    private List<UIElement> BuildConditionSections()
    {
        var secs = new List<UIElement>();
        TreeNodeConditionInfo? info = null;
        if (_node.TreeIndex >= 0)
            _graph.TreeNodeConditions.TryGetValue(_node.TreeIndex, out info);
        if (info == null) return secs;

        if (info.ConditionalAttacks.Count > 0)
        {
            var list = new StackPanel { Margin = new Thickness(10, 6, 10, 2) };
            foreach (var ca in info.ConditionalAttacks)
            {
                var row = new StackPanel { Margin = new Thickness(0, 2, 0, 4) };
                row.Children.Add(new TextBlock
                {
                    Text = ca.TagName,
                    Foreground = GreenBrush,
                    FontSize = 11,
                    FontWeight = FontWeights.Bold,
                    TextWrapping = TextWrapping.Wrap
                });
                row.Children.Add(new TextBlock
                {
                    Text = ca.AttackNames.Count > 0 ? string.Join(", ", ca.AttackNames) : "(no attacks)",
                    Foreground = DimBrush,
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(10, 1, 0, 0)
                });
                list.Children.Add(row);
            }
            secs.Add(BuildSectionCard(
                $"Conditional attacks · {info.ConditionalAttacks.Count}",
                "m_ConditionalAttacks: gameplay-tag keyed alternate attack sets the engine swaps this node into.",
                list));
        }

        if (info.RequiredTags.Count > 0)
        {
            var wrap = new WrapPanel { Margin = new Thickness(10, 8, 10, 8) };
            foreach (var tag in info.RequiredTags)
            {
                wrap.Children.Add(new Border
                {
                    Background = HeadBrush,
                    BorderBrush = BorderBrushInput,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(3),
                    Padding = new Thickness(6, 2, 6, 2),
                    Margin = new Thickness(0, 0, 6, 4),
                    Child = new TextBlock
                    {
                        Text = tag,
                        Foreground = TextBrush,
                        FontSize = 11
                    }
                });
            }
            secs.Add(BuildSectionCard(
                $"Required gameplay tags · {info.RequiredTags.Count}",
                "m_RequiredTags: this node only plays when the gameplay state carries every listed tag.",
                wrap));
        }
        return secs;
    }

    private Border BuildSectionCard(string title, string hint, UIElement content, bool viewOnly = true)
    {
        var border = new Border
        {
            Background = CardBrush,
            BorderBrush = BorderBrushDark,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Margin = new Thickness(0, 0, 0, 8)
        };
        var sp = new StackPanel();
        var header = new Border
        {
            Background = HeadBrush,
            Padding = new Thickness(10, 6, 10, 6),
            CornerRadius = new CornerRadius(4, 4, 0, 0)
        };
        var headerDock = new DockPanel();
        var badge = new TextBlock
        {
            Text = viewOnly ? "view only" : "editable",
            Foreground = viewOnly ? GrayBrush : GreenBrush,
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
            ToolTip = viewOnly
                ? "Read-only in this phase - editing lands later."
                : "Edits are stored with the project and written on export (MainChar stays view-only)."
        };
        DockPanel.SetDock(badge, Dock.Right);
        headerDock.Children.Add(badge);
        var titleTb = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        titleTb.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = FontWeights.Bold,
            Foreground = BlueBrush,
            FontSize = 12
        });
        titleTb.Children.Add(new TextBlock
        {
            Text = "  " + hint,
            Foreground = GrayBrush,
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center
        });
        headerDock.Children.Add(titleTb);
        header.Child = headerDock;
        sp.Children.Add(header);
        sp.Children.Add(content);
        border.Child = sp;
        return border;
    }

    private UIElement BuildCard()
    {
        var border = new Border
        {
            Background = CardBrush,
            BorderBrush = BorderBrushDark,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Margin = new Thickness(0, 0, 0, 8),
            Padding = new Thickness(0, 0, 0, 6)
        };
        var sp = new StackPanel();

        // --- header: struct title + live sum ---
        var header = new Border
        {
            Background = HeadBrush,
            Padding = new Thickness(10, 6, 10, 6),
            CornerRadius = new CornerRadius(4, 4, 0, 0)
        };
        var headerDock = new DockPanel();

        var sumText = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 11,
            Margin = new Thickness(10, 0, 0, 0)
        };
        DockPanel.SetDock(sumText, Dock.Right);
        headerDock.Children.Add(sumText);

        var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(new TextBlock
        {
            Text = NodeDisplay(_node),
            FontWeight = FontWeights.Bold,
            Foreground = BlueBrush,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = $"Transitions of {_node.DisplayName} (the struct export writes new links into)."
        });
        if (_structIdx >= 0 && _node.TreeIndex != _structIdx)
        {
            left.Children.Add(new TextBlock
            {
                Text = $"· writes to tree[{_structIdx}] {StructDisplay(_structIdx)}",
                Foreground = GrayBrush,
                FontSize = 11,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "A' owner-writeback: new links from this attack append into the owning redirect's struct (the rows the engine continues from)."
            });
        }
        headerDock.Children.Add(left);
        header.Child = headerDock;
        sp.Children.Add(header);

        // --- rows ---
        var rowsPanelInner = new StackPanel { Margin = new Thickness(10, 6, 10, 0) };

        var normBtn = new Button
        {
            Content = "Σ→100%",
            Style = (Style)FindResource("DarkButton"),
            FontSize = 11,
            Padding = new Thickness(8, 3, 8, 3),
            Margin = new Thickness(6, 0, 0, 0),
            ToolTip = "Scale all displayed rows proportionally to exactly 100% - keeps relative values, "
                + "and locks every row (including automatic ones) to the shown numbers."
        };
        var evenBtn = new Button
        {
            Content = "Even split",
            Style = (Style)FindResource("DarkButton"),
            FontSize = 11,
            Padding = new Thickness(8, 3, 8, 3),
            Margin = new Thickness(6, 0, 0, 0),
            ToolTip = "Divide 100% evenly across all rows. Overwrites manually typed values - "
                + "use Σ→100% to keep ratios instead."
        };
        normBtn.Click += (_, _) => { NormalizeTo100(); Rebuild(); };
        evenBtn.Click += (_, _) => { EvenSplit(); Rebuild(); };

        foreach (var r in _rows)
            rowsPanelInner.Children.Add(BuildRow(r, normBtn, evenBtn));

        var actionBar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 6, 0, 0)
        };
        actionBar.Children.Add(normBtn);
        actionBar.Children.Add(evenBtn);
        rowsPanelInner.Children.Add(actionBar);
        sp.Children.Add(rowsPanelInner);

        border.Child = sp;
        RefreshHeader(sumText, normBtn, evenBtn);
        return border;
    }

    private UIElement BuildRow(ChanceRow r, Button normBtn, Button evenBtn)
    {
        var grid = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(76) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });

        var num = new TextBlock
        {
            Text = r.RowIndex >= 0 ? (r.RowIndex + 1).ToString() : "new",
            Foreground = r.RowIndex >= 0 ? GrayBrush : WarnBrush,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 0, 0),
            ToolTip = r.RowIndex >= 0 ? $"Asset row {r.RowIndex} of struct tree[{_structIdx}]" : "Appended when this new link exports"
        };
        Grid.SetColumn(num, 0);
        grid.Children.Add(num);

        // chance (editable - multi-key rows keep their chance editable too)
        var probBox = new TextBox
        {
            Style = (Style)FindResource("DarkTextBox"),
            Width = 64,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Text = FormatProb(r.Effective),
            IsEnabled = _canEditChances,
            ToolTip = !_canEditChances
                ? "Branch chance editing is available for enemy combo trees - this row is view-only."
                : r.IsNewLink
                    ? (r.Explicit.HasValue
                        ? $"Typed value - export writes exactly {FormatProb(r.Explicit.Value)}%."
                        : "Automatic: joins the split with a default weight equal to the average existing row; export rebalances the set to 100%.")
                    : (r.Explicit.HasValue
                        ? $"Typed value - export writes exactly {FormatProb(r.Explicit.Value)}% (vanilla was {FormatProb(r.Original)}%)."
                        : $"Automatic (vanilla {FormatProb(r.Original)}%) - reshared when new links append; type to lock a value.")
        };
        r.Field = probBox;
        InputFilter.ApplyFloat(probBox);
        var normalBorder = BorderBrushInput;
        probBox.TextChanged += (_, _) =>
        {
            if (_syncing) return;
            if (float.TryParse(probBox.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v >= 0)
            {
                r.Explicit = v;
                probBox.BorderBrush = normalBorder;
                Recompute();
                UpdateRowFields(r);
                RefreshHeaderFor(probBox);
            }
            else
            {
                probBox.BorderBrush = RedBrush;
            }
            RefreshGlobalStatus();
        };
        var probCell = new DockPanel { VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(probBox, Dock.Left);
        probCell.Children.Add(probBox);
        probCell.Children.Add(new TextBlock
        {
            Text = "%",
            Foreground = GrayBrush,
            FontSize = 10,
            Margin = new Thickness(4, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        });
        Grid.SetColumn(probCell, 1);
        grid.Children.Add(probCell);

        // input
        var inputTb = new TextBlock
        {
            Text = string.IsNullOrEmpty(r.InputName) ? "(default)" : r.InputName,
            Foreground = r.RowIndex >= 0 ? DimBrush : BlueBrush,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = string.IsNullOrEmpty(r.InputName)
                ? "Input: none (picked when the previous move ends)"
                : $"Input that triggers this transition: {r.InputName}"
        };
        Grid.SetColumn(inputTb, 2);
        grid.Children.Add(inputTb);

        // target
        bool multi = r.KeyCount > 1;
        var targetTb = new TextBlock
        {
            Text = string.IsNullOrEmpty(r.TargetSummary) ? "?" : r.TargetSummary,
            Foreground = multi ? WarnBrush : TextBrush,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        string targetTip = multi
            ? $"Conditional routing ({r.KeyCount} keys): one target per condition outcome - view-only.\n{r.TargetSummary}"
            : $"Target: {r.TargetSummary}";
        if (r.ConditionPresent)
            targetTip += string.IsNullOrEmpty(r.ConditionClass)
                ? "\nHas a condition instance (availability gate)."
                : $"\nCondition: {r.ConditionClass} (availability gate).";
        if (r.ConditionParams.Count > 0)
            targetTip += "\n" + string.Join("\n", r.ConditionParams);
        targetTb.ToolTip = targetTip;
        Grid.SetColumn(targetTb, 3);
        grid.Children.Add(targetTb);

        // baseline note
        var note = new TextBlock
        {
            FontSize = 10,
            Foreground = GrayBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 4, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        if (r.IsNewLink)
            note.Text = r.Explicit.HasValue ? "typed" : "auto (new)";
        else if (r.Explicit.HasValue && Math.Abs(r.Explicit.Value - r.Original) > 0.0001f)
        {
            note.Text = $"was {FormatProb(r.Original)}%";
            note.Foreground = WarnBrush;
        }
        else note.Text = "vanilla";
        note.ToolTip = r.IsNewLink
            ? "Row appended on export (new link)."
            : $"Vanilla probability: {FormatProb(r.Original)}%";
        if (r.ConditionPresent && !string.IsNullOrEmpty(r.ConditionClass))
        {
            string condShort = r.ConditionClass;
            if (condShort.StartsWith("BP_", StringComparison.Ordinal)) condShort = condShort[3..];
            if (condShort.EndsWith("_C", StringComparison.Ordinal)) condShort = condShort[..^2];
            if (condShort.Length > 14) condShort = condShort[..14] + "..";
            note.Text += " · cond: " + condShort;
            note.ToolTip += $"\nCondition class: {r.ConditionClass}";
            if (r.ConditionParams.Count > 0)
                note.ToolTip += "\n" + string.Join("\n", r.ConditionParams);
        }
        Grid.SetColumn(note, 4);
        grid.Children.Add(note);

        return grid;
    }

    /// <summary>Push recomputed effective values into the other rows' fields (not the one
    /// being typed into) without re-triggering their TextChanged handlers.</summary>
    private void UpdateRowFields(ChanceRow except)
    {
        _syncing = true;
        try
        {
            foreach (var r in _rows)
            {
                if (ReferenceEquals(r, except) || r.Field == null) continue;
                string txt = FormatProb(r.Effective);
                if (r.Field.Text != txt) r.Field.Text = txt;
                r.Field.BorderBrush = BorderBrushInput;
            }
        }
        finally { _syncing = false; }
    }

    private void RefreshHeaderFor(TextBox source)
    {
        // Header elements are found via the card: simplest is a full refresh of badge+buttons.
        if (rowsPanel.Children.Count == 0) return;
        if (rowsPanel.Children[0] is not Border card || card.Child is not StackPanel sp) return;
        if (sp.Children.Count < 2 || sp.Children[0] is not Border header
            || header.Child is not DockPanel hd || hd.Children.Count == 0
            || hd.Children[0] is not TextBlock sumText) return;
        if (sp.Children[1] is not StackPanel rowsInner) return;
        Button? normBtn = null, evenBtn = null;
        foreach (var child in rowsInner.Children)
        {
            if (child is StackPanel bar)
            {
                foreach (var b in bar.Children.OfType<Button>())
                {
                    if (b.Content as string == "Σ→100%") normBtn = b;
                    else if (b.Content as string == "Even split") evenBtn = b;
                }
            }
        }
        if (normBtn != null && evenBtn != null) RefreshHeader(sumText, normBtn, evenBtn);
    }

    private void RefreshHeader(TextBlock sumText, Button normBtn, Button evenBtn)
    {
        float sum = _rows.Sum(r => r.Effective);
        bool ok = Math.Abs(sum - 100f) <= 0.5f;
        int edited = _rows.Count(r => r.Explicit.HasValue);
        sumText.Text = $"Σ {sum:0.###}%  ·  {_rows.Count} row{(_rows.Count == 1 ? "" : "s")}  ·  {edited} typed";
        sumText.Foreground = ok ? GreenBrush : RedBrush;
        sumText.ToolTip = ok
            ? "Displayed values sum to 100% - exactly what export writes."
            : $"Displayed values sum to {sum:0.###}%. Export normalizes automatic rows when new links append; "
              + "typed rows above 100% total leave the automatic rows at 0%.";
        normBtn.IsEnabled = _canEditChances && !ok;
        evenBtn.IsEnabled = _canEditChances && _rows.Count > 0;
    }

    private void NormalizeTo100()
    {
        if (!_canEditChances || _rows.Count == 0) return;
        Recompute();
        float sum = _rows.Sum(r => r.Effective);
        if (sum <= 0f) { EvenSplit(); return; }
        float factor = 100f / sum;
        foreach (var r in _rows) r.Effective *= factor;
        var largest = _rows.OrderByDescending(r => r.Effective).First();
        largest.Effective += 100f - _rows.Sum(r => r.Effective);
        foreach (var r in _rows) r.Explicit = r.Effective;
    }

    private void EvenSplit()
    {
        if (!_canEditChances || _rows.Count == 0) return;
        float each = 100f / _rows.Count;
        foreach (var r in _rows) { r.Explicit = each; r.Effective = each; }
    }

    private void RefreshGlobalStatus()
    {
        if (!_canEditChances)
        {
            txtStatus.Text = "View-only: chances and condition edits are available for enemy combo trees.";
            txtStatus.Foreground = GrayBrush;
            return;
        }
        int edited = _rows.Count(r => r.Explicit.HasValue);
        int condEdits = ConditionEditCount();
        float typedSum = _rows.Where(r => r.Explicit.HasValue).Sum(r => r.Explicit!.Value);
        float sum = _rows.Sum(r => r.Effective);
        string condSuffix = condEdits > 0 ? $"  ·  {condEdits} condition edit(s)" : "";

        if (_rows.Count == 0)
        {
            txtStatus.Text = _structIdx >= 0 && !_vanillaAvailable
                ? "Vanilla rows aren't in this save - existing overrides (if any) are kept untouched."
                : "Nothing to edit yet - add a new link from this node first.";
            txtStatus.Foreground = GrayBrush;
        }
        else if (typedSum > 100f + 0.5f)
        {
            txtStatus.Text = $"⚠ Typed rows total {typedSum:0.###}% - automatic rows get 0% at export. "
                + "Click Σ→100% or lower a value." + condSuffix;
            txtStatus.Foreground = RedBrush;
        }
        else if (Math.Abs(sum - 100f) > 0.5f)
        {
            txtStatus.Text = $"⚠ Displayed sum {sum:0.###}% (vanilla always sums to 100). {edited} row(s) typed." + condSuffix;
            txtStatus.Foreground = WarnBrush;
        }
        else
        {
            txtStatus.Text = (edited > 0
                ? $"{edited} row(s) typed · automatic rows rebalance around them on export."
                : "No changes yet · new links join the split automatically on export.") + condSuffix;
            txtStatus.Foreground = GrayBrush;
        }
    }

    private static string FormatProb(float v)
        => Math.Abs(v - Math.Round(v)) < 0.0001f
            ? ((int)Math.Round(v)).ToString(CultureInfo.InvariantCulture)
            : v.ToString("0.###", CultureInfo.InvariantCulture);

    private Dictionary<int, Dictionary<int, float>> BuildOverrides()
    {
        var result = _graph.TreeRowProbabilityOverrides
            .ToDictionary(kv => kv.Key, kv => new Dictionary<int, float>(kv.Value));
        if (_structIdx >= 0 && _vanillaAvailable)
        {
            var map = new Dictionary<int, float>();
            foreach (var r in _rows.Where(r => !r.IsNewLink && r.Explicit.HasValue))
            {
                if (r.Explicit is float ev) map[r.RowIndex] = ev;
            }
            if (map.Count > 0) result[_structIdx] = map;
            else result.Remove(_structIdx);
        }
        return result;
    }

    private bool SameAsGraph(Dictionary<int, Dictionary<int, float>> overrides)
    {
        var current = _graph.TreeRowProbabilityOverrides;
        if (current.Count != overrides.Count) return false;
        foreach (var kvp in current)
        {
            if (!overrides.TryGetValue(kvp.Key, out var other) || other.Count != kvp.Value.Count)
                return false;
            foreach (var row in kvp.Value)
            {
                if (!other.TryGetValue(row.Key, out var v) || Math.Abs(v - row.Value) > 0.0001f)
                    return false;
            }
        }
        foreach (var r in _rows.Where(r => r.IsNewLink && r.Edge != null))
        {
            float? cur = r.Edge!.NewLinkProbability;
            if (cur.HasValue != r.Explicit.HasValue) return false;
            if (cur is float c && r.Explicit is float ev && Math.Abs(c - ev) > 0.0001f) return false;
        }
        return true;
    }

    private Dictionary<int, Dictionary<string, string>> BuildConditionParams()
    {
        var result = _graph.TreeConditionParamOverrides
            .ToDictionary(kv => kv.Key, kv => new Dictionary<string, string>(kv.Value));
        foreach (var (expIdx, local) in _condParamEdits)
        {
            if (local.Count > 0) result[expIdx] = new Dictionary<string, string>(local);
            else result.Remove(expIdx);   // touched instance fully reverted to vanilla
        }
        return result;
    }

    private Dictionary<int, Dictionary<int, TreeRowConditionEdit>> BuildRowConditions()
    {
        var result = _graph.TreeRowConditionOverrides.ToDictionary(
            kv => kv.Key,
            kv => kv.Value.ToDictionary(
                r => r.Key,
                r => r.Value.Clone()));
        if (_structIdx >= 0)
        {
            if (!result.TryGetValue(_structIdx, out var mine))
                result[_structIdx] = mine = new Dictionary<int, TreeRowConditionEdit>();
            foreach (var (rowIdx, e) in _condRowEdits)
            {
                if (e.ClearCondition || e.Retarget.Count > 0 || e.RetargetNodeIds.Count > 0
                    || e.HasPendingInstance || e.AddedKeys.Count > 0)
                    mine[rowIdx] = e.Clone();
                else mine.Remove(rowIdx);   // touched row fully reverted to vanilla
            }
            if (mine.Count == 0) result.Remove(_structIdx);
        }
        return result;
    }

    private static TreeRowConditionEdit CloneEdit(TreeRowConditionEdit e) => e.Clone();

    /// <summary>Fresh edge-condition dict for commit: graph entries whose edge still
    /// exists (pending mixup removals count as gone, staged ones as live - OK runs
    /// before the graph merge), plus touched edge rows that carry a pending instance
    /// (ClearCondition is meaningless on rows that do not exist yet - Remove drops the
    /// edit instead).</summary>
    private Dictionary<string, TreeRowConditionEdit> BuildEdgeConditions()
    {
        var result = new Dictionary<string, TreeRowConditionEdit>();
        var liveKeys = new HashSet<string>(_graph.Edges.Select(ComboGraph.EdgeConditionKey));
        foreach (var e in _removedMixupEdges) liveKeys.Remove(ComboGraph.EdgeConditionKey(e));
        foreach (var e in _stagedEdges) liveKeys.Add(ComboGraph.EdgeConditionKey(e));
        // Merged links ride the target row's AddedKeys - a standalone condition staged
        // on them (this session or saved earlier) must not be written too.
        foreach (var kv in _stagedMerges)
            if (kv.Value.HasValue) liveKeys.Remove(ComboGraph.EdgeConditionKey(kv.Key));
        foreach (var e in _graph.Edges)
            if (e.MergedIntoStruct >= 0) liveKeys.Remove(ComboGraph.EdgeConditionKey(e));
        foreach (var kvp in _graph.TreeEdgeConditionOverrides)
            if (liveKeys.Contains(kvp.Key))
                result[kvp.Key] = CloneEdit(kvp.Value);
        foreach (var (key, edit) in _edgeCondEdits)
        {
            if (!liveKeys.Contains(key)) continue;
            if (edit.HasPendingInstance && !edit.ClearCondition)
                result[key] = CloneEdit(edit);
            else
                result.Remove(key);
        }
        return result;
    }

    private static bool SameEdgeConds(
        Dictionary<string, TreeRowConditionEdit> a,
        Dictionary<string, TreeRowConditionEdit> b)
    {
        if (a.Count != b.Count) return false;
        foreach (var kvp in a)
        {
            if (!b.TryGetValue(kvp.Key, out var o)) return false;
            if (o.ClearCondition != kvp.Value.ClearCondition) return false;
            if (o.Retarget.Count != kvp.Value.Retarget.Count) return false;
            foreach (var rt in kvp.Value.Retarget)
                if (!o.Retarget.TryGetValue(rt.Key, out var v) || v != rt.Value) return false;
            if (o.RetargetNodeIds.Count != kvp.Value.RetargetNodeIds.Count) return false;
            foreach (var rt in kvp.Value.RetargetNodeIds)
                if (!o.RetargetNodeIds.TryGetValue(rt.Key, out var v) || v != rt.Value) return false;
            if (o.SetInstance != kvp.Value.SetInstance) return false;
            if (!string.Equals(o.CloneSourceRel ?? "", kvp.Value.CloneSourceRel ?? "",
                    StringComparison.Ordinal)) return false;
            if (o.CloneSourceExport != kvp.Value.CloneSourceExport) return false;
        }
        return true;
    }

    private static bool SameCondParams(Dictionary<int, Dictionary<string, string>> a,
        Dictionary<int, Dictionary<string, string>> b)
    {
        if (a.Count != b.Count) return false;
        foreach (var kvp in a)
        {
            if (!b.TryGetValue(kvp.Key, out var ob) || ob.Count != kvp.Value.Count) return false;
            foreach (var kv in kvp.Value)
                if (!ob.TryGetValue(kv.Key, out var ov) || ov != kv.Value) return false;
        }
        return true;
    }

    private static bool SameRowConds(Dictionary<int, Dictionary<int, TreeRowConditionEdit>> a,
        Dictionary<int, Dictionary<int, TreeRowConditionEdit>> b)
    {
        if (a.Count != b.Count) return false;
        foreach (var kvp in a)
        {
            if (!b.TryGetValue(kvp.Key, out var ob) || ob.Count != kvp.Value.Count) return false;
            foreach (var kv in kvp.Value)
            {
                if (!ob.TryGetValue(kv.Key, out var o) || o.ClearCondition != kv.Value.ClearCondition
                    || o.SeededCollapse != kv.Value.SeededCollapse)
                    return false;
                if (o.Retarget.Count != kv.Value.Retarget.Count) return false;
                foreach (var rt in kv.Value.Retarget)
                    if (!o.Retarget.TryGetValue(rt.Key, out var v) || v != rt.Value) return false;
                if (o.RetargetNodeIds.Count != kv.Value.RetargetNodeIds.Count) return false;
                foreach (var rt in kv.Value.RetargetNodeIds)
                    if (!o.RetargetNodeIds.TryGetValue(rt.Key, out var v) || v != rt.Value) return false;
                if (o.AddedKeys.Count != kv.Value.AddedKeys.Count) return false;
                foreach (var ak in kv.Value.AddedKeys)
                    if (!o.AddedKeys.TryGetValue(ak.Key, out var av) || av != ak.Value) return false;
                if (o.SetInstance != kv.Value.SetInstance) return false;
                if (!string.Equals(o.CloneSourceRel ?? "", kv.Value.CloneSourceRel ?? "",
                        StringComparison.Ordinal)) return false;
                if (o.CloneSourceExport != kv.Value.CloneSourceExport) return false;
            }
        }
        return true;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        // Sum gate (mirrors Start Entries): a touched set that no longer totals 100
        // - typically a link deleted after probabilities were typed - would ship a
        // dead roll zone. Vanilla-untouched rows never block (vanilla sums to 100).
        if (_canEditChances && _rows.Count > 0)
        {
            Recompute();
            bool touched = _rows.Any(r => r.Explicit.HasValue
                && (!r.IsNewLink && Math.Abs(r.Explicit.Value - r.Original) > 0.001f
                    || r.IsNewLink));
            float sum = _rows.Sum(r => r.Effective);
            if (touched && Math.Abs(sum - 100f) > 0.5f)
            {
                MessageBox.Show(this,
                    $"Branch chances for '{NodeDisplay(_node)}' sum to {sum:0.###}%. "
                    + "Vanilla always sums to 100%.\n\nClick Σ→100% or adjust the values, "
                    + "then save again.",
                    "Probabilities don't sum to 100",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                txtStatus.Text = $"⚠ Sum {sum:0.###}% - fix to 100% before saving.";
                txtStatus.Foreground = RedBrush;
                return;
            }
        }
        var overrides = BuildOverrides();
        var condParams = BuildConditionParams();
        var condRows = BuildRowConditions();
        var edgeConds = BuildEdgeConditions();
        if (SameAsGraph(overrides) && SameCondParams(condParams, _graph.TreeConditionParamOverrides)
            && SameRowConds(condRows, _graph.TreeRowConditionOverrides)
            && SameEdgeConds(edgeConds, _graph.TreeEdgeConditionOverrides)
            && _stagedEdges.Count == 0 && _removedMixupEdges.Count == 0
            && _stagedMerges.Count == 0)
        {
            DialogResult = false;
            return;
        }
        ResultOverrides = overrides;
        ResultConditionParams = condParams;
        ResultRowConditions = condRows;
        ResultEdgeConditions = edgeConds;
        ResultEdgeProbabilities = _rows
            .Where(r => r.IsNewLink && r.Edge != null)
            .Select(r => (r.Edge!, r.Explicit))
            .ToList();
        ResultNewEdges = _stagedEdges.ToList();
        ResultRemovedEdges = _removedMixupEdges.ToList();
        ResultMergedEdges = _stagedMerges
            .Where(kv => kv.Value.HasValue)
            .Select(kv => (kv.Key, _structIdx, kv.Value!.Value.rowIdx, kv.Value!.Value.key))
            .ToList();
        ResultUnmergedEdges = _stagedMerges
            .Where(kv => !kv.Value.HasValue)
            .Select(kv => kv.Key)
            .ToList();
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
