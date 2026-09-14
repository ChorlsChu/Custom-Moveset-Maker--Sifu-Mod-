using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using SifuMovesetEditor;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;

namespace SifuMovesetEditor.Export;

public partial class ExportDialog : Window
{
    private readonly List<ComboNode> _modifiedNodes;
    private readonly string _contentPath;
    private readonly string _outputPath;
    private readonly Dictionary<string, string> _animToDbPath;
    private readonly string? _activeStance;
    private readonly string? _charTransitionPath;
    private readonly string? _charBaseMovementDBPath;
    private readonly string? _referenceModDir;
    private readonly string? _enemyComboPath;
    private readonly Dictionary<string, (float hitFrame, int buildupFrame)> _animToTiming;
    private ComboGraph? _graph;
    private Dictionary<int, string>? _customClones;
    private readonly bool _enableCustomTreeAppend = false; // Disabled: combo tree re-serialization corrupts m_Transitions
    private string? _pakPath;
    private string? _outputDir;

    private static Brush MakeBrush(string hex) =>
        (Brush)new BrushConverter().ConvertFrom(hex);

    public ExportDialog(
        List<ComboNode> modifiedNodes,
        string contentPath,
        string outputPath,
        Dictionary<string, string> animToDbPath,
        string? activeStance = null,
        string? charTransitionPath = null,
        string? charBaseMovementDBPath = null,
        string? referenceModDir = null,
        ComboGraph? graph = null,
        Dictionary<int, string>? customClones = null,
        string? enemyComboPath = null,
        Dictionary<string, (float hitFrame, int buildupFrame)>? animToTiming = null)
    {
        InitializeComponent();

        _modifiedNodes = modifiedNodes;
        _contentPath = contentPath;
        _outputPath = outputPath;
        _animToDbPath = animToDbPath;
        _activeStance = activeStance;
        _charTransitionPath = charTransitionPath;
        _charBaseMovementDBPath = charBaseMovementDBPath;
        _referenceModDir = referenceModDir;
        _enemyComboPath = enemyComboPath;
        _animToTiming = animToTiming ?? new();
        _graph = graph;
        _customClones = customClones;

        LoadReview();
    }

    private void LoadReview()
    {
        var entries = new List<object>();

        if (!string.IsNullOrEmpty(_activeStance) && _activeStance != "MainChar")
        {
            entries.Add(new { DisplayName = $"Combat Stance \u2192 {_activeStance}",
                              DefaultAnimPath = "MainChar (vanilla)",
                              AnimPath = $"{_activeStance} (BaseMovementDB + BP_TransitionAnimRequest)" });
        }

        entries.AddRange(_modifiedNodes);

        txtChangeCount.Text = $"{entries.Count} change(s) detected:";
        lstChanges.ItemsSource = entries;

        var outputDir = string.IsNullOrEmpty(_outputPath)
            ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ExportedMods")
            : _outputPath;

        txtModName.Text = "MainCharComboMod";
        UpdateOutputPreview(outputDir);
    }

    private void UpdateOutputPreview(string outputDir)
    {
        var name = GetModFileName();
        txtOutput.Text = $"Output: {outputDir}/{name}.pak + .sig";
    }

    private string GetModBaseName()
    {
        var raw = txtModName?.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(raw)) return "MainCharComboMod";

        foreach (var c in Path.GetInvalidFileNameChars())
            raw = raw.Replace(c, '_');

        if (raw.EndsWith(".pak", StringComparison.OrdinalIgnoreCase))
            raw = raw.Substring(0, raw.Length - 4);

        return string.IsNullOrEmpty(raw) ? "MainCharComboMod" : raw;
    }

    private string GetModFileName() => GetModBaseName() + ".pak";

    private void txtModName_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        var outputDir = string.IsNullOrEmpty(_outputPath)
            ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ExportedMods")
            : _outputPath;
        UpdateOutputPreview(outputDir);
    }

    private async void Confirm_Click(object sender, RoutedEventArgs e)
    {
        panelReview.Visibility = Visibility.Collapsed;
        panelExporting.Visibility = Visibility.Visible;

        try
        {
            var contentPath = _contentPath;
            if (string.IsNullOrEmpty(contentPath) || !Directory.Exists(contentPath))
            {
                ShowError("Content path not set or invalid. Check Settings.");
                return;
            }

            var outputPath = string.IsNullOrEmpty(_outputPath)
                ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ExportedMods")
                : _outputPath;
            _outputDir = outputPath;
            Directory.CreateDirectory(outputPath);

            var gameRoot = Path.Combine(contentPath, "Content");
            if (!Directory.Exists(gameRoot))
            {
                ShowError($"Game content not found at: {gameRoot}");
                return;
            }

            var fileEntries = new List<(string src, string dest)>();
            var hasComboChanges = _modifiedNodes.Count > 0;
            var hasStanceChange = !string.IsNullOrEmpty(_activeStance) && _activeStance != "MainChar";

            ErrorLog.Write("EXPORT", new Exception($"=== EXPORT START: {_modifiedNodes.Count} modified nodes, stance={_activeStance ?? "MainChar"} ==="));

            // Phase 1: Combo tree patching (only if combo nodes changed)
            if (hasComboChanges)
            {
                UpdateStep(1, "active");
                txtCurrentAction.Text = "Locating vanilla combo tree...";
                SetProgress(10);

                var vanillaAssetPath = Path.Combine(gameRoot, "DB", "_MainChar", "Combos", "MainChar_ComboTree.uasset");
                if (!File.Exists(vanillaAssetPath))
                {
                    ShowError($"Vanilla asset not found: {vanillaAssetPath}");
                    return;
                }

                UpdateStep(1, "done");
                SetProgress(25);

                UpdateStep(2, "active");
                txtCurrentAction.Text = "Patching combo tree imports...";

                var outputDirForAsset = Path.Combine(outputPath, "Sifu", "Content", "DB", "_MainChar", "Combos");
                Directory.CreateDirectory(outputDirForAsset);
                var outUasset = Path.Combine(outputDirForAsset, "MainChar_ComboTree.uasset");

                bool comboTreeModified = false;
                await System.Threading.Tasks.Task.Run(() =>
                {
                    var eng = EngineVersion.VER_UE4_26;
                    var asset = new UAsset(vanillaAssetPath, eng, null, CustomSerializationFlags.None);

                    NormalExport? comboExport = null;
                    for (int i = 0; i < asset.Exports.Count; i++)
                    {
                        if (asset.Exports[i] is NormalExport ne && ne.SerialSize > 50000)
                        {
                            comboExport = ne;
                            break;
                        }
                    }

                    if (comboExport == null)
                        throw new Exception("Could not find Combo export (no export > 50KB)");

                    var allMaps = FindAllMapsNamed(comboExport.Data, "m_Attacks");
                    ErrorLog.Write("EXPORT", new Exception($"Found {allMaps.Count} m_Attacks maps"));

                    int patched = 0;
                    int skippedEmpty = 0;
                    int skippedNoDb = 0;
                    int patchedFallback = 0;
                    var patchedDbFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var node in _modifiedNodes)
                    {
                        // Enemy nodes: handled by combo tree Import swap below (same approach as MainChar Phase 2)
                        // Skip DB in-place patching — the enemy combo tree's imports are redirected instead
                        if (!string.IsNullOrEmpty(node.DefaultDBPath) && node.DefaultDBPath.Contains("/AI/Archetypes/", StringComparison.OrdinalIgnoreCase) && _graph != null && !string.Equals(_graph.WeaponName, "MainChar", StringComparison.OrdinalIgnoreCase))
                        {
                            ErrorLog.Write("EXPORT", new Exception($"  ENEMY DEFER: {node.DisplayName} -> combo tree Import swap"));
                            continue;
                        }
                        if (string.IsNullOrEmpty(node.DefaultDBPath))
                        {
                            // Option B: clone template AttackDB for custom nodes (e.g. FireDisciple moves without AttackDB)
                            if (node.TreeIndex == -1 && !string.IsNullOrEmpty(node.AnimPath))
                            {
                                var templateRel = "DB/_MainChar/Combos/Attacks/BareHands/LightCombo/MainChar_Jab_FR.uasset";
                                var templateFile = Path.Combine(gameRoot, templateRel);
                                if (!File.Exists(templateFile))
                                    templateFile = Directory.GetFiles(Path.Combine(gameRoot, "DB"), "MainChar_Jab*.uasset", SearchOption.AllDirectories).FirstOrDefault();
                                if (templateFile != null && File.Exists(templateFile))
                                {
                                    var cloneRel = $"DB/_MainChar/Combos/Attacks/Custom/MainChar_Custom_{node.Id}";
                                    var cloneOut = Path.Combine(outputPath, "Sifu", "Content", cloneRel + ".uasset");
                                    Directory.CreateDirectory(Path.GetDirectoryName(cloneOut)!);
                                    if (PatchDbAnimation(templateFile, node.AnimPath, cloneOut, EngineVersion.VER_UE4_26))
                                    {
                                        var cloneOutUexp = Path.ChangeExtension(cloneOut, ".uexp");
                                        var templateUexp = Path.ChangeExtension(templateFile, ".uexp");
                                        if (File.Exists(templateUexp) && !File.Exists(cloneOutUexp))
                                            File.Copy(templateUexp, cloneOutUexp, true);
                                        fileEntries.Add((cloneOut, $"../../../Sifu/Content/{cloneRel}.uasset"));
                                        fileEntries.Add((cloneOutUexp, $"../../../Sifu/Content/{cloneRel}.uexp"));
                                        patched++;
                                        ErrorLog.Write("EXPORT", new Exception($"  CLONED DB for {node.DisplayName} -> {cloneRel} (anim {node.AnimPath})"));
                                        // TODO: append new node to combo tree m_Nodes + inject edge Input — next iteration
                                        continue;
                                    }
                                }
                            }
                            skippedEmpty++;
                            if (skippedEmpty <= 3)
                                ErrorLog.Write("EXPORT", new Exception($"  SKIP(empty DB): {node.DisplayName} AnimPath='{node.AnimPath}' DefaultDBPath='{node.DefaultDBPath}' DefaultAnimPath='{node.DefaultAnimPath}'"));
                            continue;
                        }
                        if (!_animToDbPath.TryGetValue(node.AnimPath, out var newDbPath))
                        {
                            // Patch the DB the combo tree references (DefaultDBPath), not where the animation came from
                            var fallbackDbPath = node.DefaultDBPath;
                            var relDbPath = fallbackDbPath.TrimStart('/');
                            if (relDbPath.StartsWith("Game/", StringComparison.OrdinalIgnoreCase))
                                relDbPath = relDbPath.Substring(5);
                            var vanillaDbFile = Path.Combine(gameRoot, relDbPath + ".uasset");
                            if (!patchedDbFiles.Contains(fallbackDbPath) && File.Exists(vanillaDbFile))
                            {
                                var outDbPath = Path.Combine(outputPath, "Sifu", "Content", relDbPath + ".uasset");

                                ErrorLog.Write("EXPORT", new Exception($"  FALLBACK ATTEMPT: {node.DisplayName} vanillaDb='{vanillaDbFile}' (source={fallbackDbPath})"));

                                if (PatchDbAnimation(vanillaDbFile, node.AnimPath, outDbPath, EngineVersion.VER_UE4_26))
                                {
                                    patchedDbFiles.Add(fallbackDbPath);
                                    var outDbUexp = Path.ChangeExtension(outDbPath, ".uexp");
                                    var vanillaDbUexp = Path.ChangeExtension(vanillaDbFile, ".uexp");
                                    if (File.Exists(vanillaDbUexp) && !File.Exists(outDbUexp))
                                        File.Copy(vanillaDbUexp, outDbUexp, true);

                                    fileEntries.Add((outDbPath, "../../../Sifu/Content/" + relDbPath + ".uasset"));
                                    fileEntries.Add((outDbUexp, "../../../Sifu/Content/" + relDbPath + ".uexp"));

                                    patchedFallback++;
                                    patched++;
                                    ErrorLog.Write("EXPORT", new Exception($"  PATCHED (fallback): {node.DisplayName} -> {node.AnimPath} (modified {Path.GetFileName(fallbackDbPath)})"));
                                    continue;
                                }
                            }
                            else
                            {
                                ErrorLog.Write("EXPORT", new Exception($"  FALLBACK SKIP: {node.DisplayName} vanillaDb='{vanillaDbFile}' exists={File.Exists(vanillaDbFile)} alreadyPatched={patchedDbFiles.Contains(fallbackDbPath)}"));
                            }

                            skippedNoDb++;
                            if (skippedNoDb <= 3)
                                ErrorLog.Write("EXPORT", new Exception($"  SKIP(no anim->db): {node.DisplayName} AnimPath='{node.AnimPath}' DefaultDBPath='{node.DefaultDBPath}'"));
                            continue;
                        }

                        // MainChar: patch original DB in-place instead of modifying combo tree
                        // Combo tree re-serialization via asset.Write() corrupts m_Transitions, breaking combo chains
                        {
                            var inplaceDbPath = node.DefaultDBPath;
                            var inplaceRel = inplaceDbPath.TrimStart('/');
                            if (inplaceRel.StartsWith("Game/", StringComparison.OrdinalIgnoreCase))
                                inplaceRel = inplaceRel.Substring(5);
                            var inplaceVanilla = Path.Combine(gameRoot, inplaceRel + ".uasset");
                            if (!patchedDbFiles.Contains(inplaceDbPath) && File.Exists(inplaceVanilla))
                            {
                                var inplaceOut = Path.Combine(outputPath, "Sifu", "Content", inplaceRel + ".uasset");
                                if (PatchDbAnimation(inplaceVanilla, node.AnimPath, inplaceOut, EngineVersion.VER_UE4_26))
                                {
                                    patchedDbFiles.Add(inplaceDbPath);
                                    var inplaceUexp = Path.ChangeExtension(inplaceOut, ".uexp");
                                    var inplaceVanillaUexp = Path.ChangeExtension(inplaceVanilla, ".uexp");
                                    if (File.Exists(inplaceVanillaUexp) && !File.Exists(inplaceUexp))
                                        File.Copy(inplaceVanillaUexp, inplaceUexp, true);
                                    fileEntries.Add((inplaceOut, "../../../Sifu/Content/" + inplaceRel + ".uasset"));
                                    fileEntries.Add((inplaceUexp, "../../../Sifu/Content/" + inplaceRel + ".uexp"));
                                    patched++;
                                    ErrorLog.Write("EXPORT", new Exception($"  PATCHED (in-place): {node.DisplayName} -> {node.AnimPath} (modified {Path.GetFileName(inplaceDbPath)})"));
                                    continue;
                                }
                            }
                            skippedNoDb++;
                            if (skippedNoDb <= 3)
                                ErrorLog.Write("EXPORT", new Exception($"  SKIP(in-place failed): {node.DisplayName} AnimPath='{node.AnimPath}' DB='{inplaceDbPath}' exists={File.Exists(inplaceVanilla)}"));
                            continue;
                        }

                        var normalizedSlotKey = NormalizeSlotKey(node.DefaultDBPath);
                        var attackName = Path.GetFileNameWithoutExtension(newDbPath);
                        var attackPath = EnsureLeadingSlash(newDbPath);

                        if (string.IsNullOrEmpty(attackName) || string.IsNullOrEmpty(attackPath)) continue;

                        bool found = false;
                        foreach (var attacksMap in allMaps)
                        {
                            foreach (var kvp in attacksMap.Value)
                            {
                                string keyStr = GetKeyString(kvp.Key);
                                if (keyStr == normalizedSlotKey)
                                {
                                    var valData = kvp.Value as ObjectPropertyData;
                                    if (valData == null) continue;

                                    int importIdx = FindImport(asset, attackName, attackPath);
                                    if (importIdx < 0)
                                        importIdx = AddAttackDBImport(asset, attackName, attackPath);

                                    valData.Value = FPackageIndex.FromImport(importIdx);
                                    patched++;
                                    ErrorLog.Write("EXPORT", new Exception($"  PATCHED: {node.DisplayName} -> {attackName} (import[{importIdx}])"));
                                    found = true;
                                    break;
                                }
                            }
                            if (found) break;
                        }

                        if (!found)
                            ErrorLog.Write("EXPORT", new Exception($"  NOT FOUND: {node.DisplayName} slot key '{normalizedSlotKey}'"));
                    }

                    // Phase 1b: SKIPPED — testing combo-only export without DataTable timing patches
                    if (false)
                    {
                        var enemyNodes = _modifiedNodes
                            .Where(n => !string.IsNullOrEmpty(n.DefaultDBPath) && n.DefaultDBPath.Contains("/AI/Archetypes/", StringComparison.OrdinalIgnoreCase) && _graph != null && !string.Equals(_graph.WeaponName, "MainChar", StringComparison.OrdinalIgnoreCase))
                            .Select(n =>
                            {
                                var targetRelDb = n.DefaultDBPath.TrimStart('/');
                                if (targetRelDb.StartsWith("Game/", StringComparison.OrdinalIgnoreCase)) targetRelDb = targetRelDb.Substring(5);
                                var gameAttackDir = Path.GetDirectoryName(Path.Combine(gameRoot, targetRelDb + ".uasset"));
                                return (node: n, targetRelDb, gameAttackDir, targetAttackName: Path.GetFileNameWithoutExtension(targetRelDb));
                            })
                            .Where(x => x.gameAttackDir != null)
                            .ToList();

                        // Group by DataTable output path to load each DataTable once
                        var dtGroups = new Dictionary<string, (string vanillaDtPath, string outDtPath, List<(string rowName, string animPath)> rows)>(StringComparer.OrdinalIgnoreCase);
                        foreach (var (node, targetRelDb, gameAttackDir, targetAttackName) in enemyNodes)
                        {
                            var searchDir = gameAttackDir;
                            for (int level = 0; level < 4 && !string.IsNullOrEmpty(searchDir); level++)
                            {
                                if (!Directory.Exists(searchDir)) break;
                                foreach (var dtFile in Directory.GetFiles(searchDir, "*.uasset"))
                                {
                                    var dtName = Path.GetFileNameWithoutExtension(dtFile);
                                    if (dtName.Equals(targetAttackName, StringComparison.OrdinalIgnoreCase)) continue;
                                    if (dtName.Contains("HitBoxData", StringComparison.OrdinalIgnoreCase)) continue;
                                    if (!dtName.Contains("Datatable", StringComparison.OrdinalIgnoreCase) && !dtName.Contains("AttackData", StringComparison.OrdinalIgnoreCase) && !dtName.EndsWith("_Attacks", StringComparison.OrdinalIgnoreCase)) continue;

                                    var outDt = Path.Combine(outputPath, "Sifu", "Content", Path.GetRelativePath(gameRoot, dtFile));
                                    if (!dtGroups.TryGetValue(outDt, out var group))
                                    {
                                        group = (dtFile, outDt, new List<(string, string)>());
                                        dtGroups[outDt] = group;
                                    }
                                    group.rows.Add((targetAttackName, node.AnimPath));
                                    ErrorLog.Write("EXPORT", new Exception($"  DT CANDIDATE: {dtName} row '{targetAttackName}' anim '{node.AnimPath}' (found at level {level})"));
                                }
                                var parent = Path.GetDirectoryName(searchDir);
                                if (parent == searchDir) break;
                                searchDir = parent;
                            }
                        }

                        foreach (var (outDt, (vanillaDtPath, outDtPath, rows)) in dtGroups)
                        {
                            try
                            {
                                var dtAsset = new UAsset(vanillaDtPath, EngineVersion.VER_UE4_26, null, CustomSerializationFlags.None);
                                NormalExport? dtExport = null;
                                foreach (var exp in dtAsset.Exports)
                                {
                                    if (exp is NormalExport ne && ne.Data != null) { dtExport = ne; break; }
                                }
                                if (dtExport == null) { ErrorLog.Write("EXPORT", new Exception($"  DT SKIP: No NormalExport in {Path.GetFileName(vanillaDtPath)}")); continue; }

                                var dataProps = dtExport.Data;
                                if (dataProps == null) { ErrorLog.Write("EXPORT", new Exception($"  DT SKIP: Data is null in {Path.GetFileName(vanillaDtPath)}")); continue; }

                                // Debug: dump all top-level property names and types
                                ErrorLog.Write("EXPORT", new Exception($"  DT STRUCTURE in {Path.GetFileNameWithoutExtension(vanillaDtPath)}: {string.Join(", ", dataProps.Select(p => $"{p.Name?.Value}({p.GetType().Name})"))}"));

                                // Debug: dump all row names in this DataTable
                                var allRowNames = new List<string>();
                                foreach (var prop in dataProps)
                                {
                                    if (prop is ArrayPropertyData arr && arr.Name?.Value?.ToString() == "RowMap")
                                    {
                                        ErrorLog.Write("EXPORT", new Exception($"  DT RowMap is ArrayPropertyData with {arr.Value?.Length} elements"));
                                        foreach (var elem in arr.Value)
                                        {
                                            if (elem is StructPropertyData rowStruct)
                                            {
                                                var rn = rowStruct.Name?.Value?.ToString() ?? "(null)";
                                                allRowNames.Add(rn);
                                            }
                                            else
                                            {
                                                ErrorLog.Write("EXPORT", new Exception($"  DT RowMap elem type: {elem?.GetType().Name} name={elem?.Name?.Value}"));
                                            }
                                        }
                                    }
                                    else if (prop is MapPropertyData map && map.Name?.Value?.ToString() == "RowMap")
                                    {
                                        ErrorLog.Write("EXPORT", new Exception($"  DT RowMap is MapPropertyData, type={map.GetType().FullName}"));
                                        try
                                        {
                                            foreach (var kvp in map.Value)
                                            {
                                                allRowNames.Add(kvp.Key?.ToString() ?? "(null)");
                                            }
                                        }
                                        catch (Exception mapEx)
                                        {
                                            ErrorLog.Write("EXPORT", new Exception($"  DT MapPropertyData iteration failed: {mapEx.Message}"));
                                        }
                                    }
                                    else if (prop is SetPropertyData set && set.Name?.Value?.ToString() == "RowMap")
                                    {
                                        ErrorLog.Write("EXPORT", new Exception($"  DT RowMap is SetPropertyData"));
                                    }
                                }
                                ErrorLog.Write("EXPORT", new Exception($"  DT ROWS in {Path.GetFileNameWithoutExtension(vanillaDtPath)}: [{string.Join(", ", allRowNames.Take(20))}{(allRowNames.Count > 20 ? ", ..." : "")}] ({allRowNames.Count} total)"));

                                int patchedRows = 0;
                                var binaryPatchRows = new List<(string rowName, string animPath)>();
                                foreach (var (rowName, animPath) in rows)
                                {
                                    bool found = false;
                                    foreach (var prop in dataProps)
                                    {
                                        if (prop is ArrayPropertyData arr && arr.Name?.Value?.ToString() == "RowMap")
                                        {
                                            foreach (var elem in arr.Value)
                                            {
                                                if (elem is StructPropertyData rowStruct)
                                                {
                                                    var rn = rowStruct.Name?.Value?.ToString();
                                                    if (rn == rowName)
                                                    {
                                                        if (PatchRowAnim(rowStruct, animPath, dtAsset))
                                                        {
                                                            if (_animToTiming != null) PatchRowTiming(rowStruct, animPath, _animToTiming, dtAsset);
                                                            patchedRows++;
                                                            ErrorLog.Write("EXPORT", new Exception($"  PATCHED DataTable row '{rowName}' -> {Path.GetFileName(animPath)}"));
                                                        }
                                                        found = true;
                                                        break;
                                                    }
                                                }
                                            }
                                            if (found) break;
                                        }
                                    }
                                    if (!found)
                                    {
                                        if (_animToTiming != null && _animToTiming.TryGetValue(animPath, out _))
                                        {
                                            binaryPatchRows.Add((rowName, animPath));
                                        }
                                        else
                                        {
                                            ErrorLog.Write("EXPORT", new Exception($"  DT ROW NOT FOUND: '{rowName}' in {Path.GetFileNameWithoutExtension(vanillaDtPath)}"));
                                        }
                                    }
                                }

                                if (patchedRows > 0 || binaryPatchRows.Count > 0)
                                {
                                    Directory.CreateDirectory(Path.GetDirectoryName(outDtPath)!);
                                    dtAsset.Write(outDtPath);
                                    var outDtUexp = Path.ChangeExtension(outDtPath, ".uexp");
                                    var inDtUexp = Path.ChangeExtension(vanillaDtPath, ".uexp");
                                    if (File.Exists(inDtUexp) && !File.Exists(outDtUexp)) File.Copy(inDtUexp, outDtUexp, true);

                                    foreach (var (rowName, animPath) in binaryPatchRows)
                                    {
                                        BinaryPatchDataTableTiming(vanillaDtPath, outDtPath, rowName, 0, 0, _animToTiming!, animPath);
                                        patchedRows++;
                                    }

                                    var stagingContentDir = Path.Combine(outputPath, "Sifu", "Content");
                                    var relDt = Path.GetRelativePath(stagingContentDir, outDtPath).Replace('\\', '/');
                                    fileEntries.Add((outDtPath, "../../../Sifu/Content/" + relDt));
                                    fileEntries.Add((outDtUexp, "../../../Sifu/Content/" + relDt.Replace(".uasset", ".uexp")));
                                    ErrorLog.Write("EXPORT", new Exception($"  Wrote DataTable {Path.GetFileNameWithoutExtension(outDtPath)}: {patchedRows}/{rows.Count} rows patched ({new FileInfo(outDtPath).Length}B)"));
                                }
                            }
                            catch (Exception ex)
                            {
                                ErrorLog.Write("EXPORT", new Exception($"  DT FAIL: {Path.GetFileName(vanillaDtPath)}: {ex.Message}"));
                            }
                        }
                    }

                    // Append new custom nodes to m_Nodes and inject edges
                    if (_graph != null && _enableCustomTreeAppend)
                    {
                        try
                        {
                            var newNodes = _graph.Nodes.Where(n => n.TreeIndex == -1).ToList();
                            if (newNodes.Count > 0)
                            {
                                var nodesArr = comboExport.Data.OfType<ArrayPropertyData>().FirstOrDefault(p => p.Name.Value.ToString() == "m_Nodes");
                                if (nodesArr != null && nodesArr.Value != null && nodesArr.Value.Length > 0)
                                {
                                    var idToTreeIndex = new Dictionary<int,int>();
                                    foreach (var n in _graph.Nodes.Where(n=>n.TreeIndex>=0))
                                        idToTreeIndex[n.Id] = n.TreeIndex;
                                    int baseCount = nodesArr.Value.Length;
                                    // pick first non-conduit real combo node as template, not Value[0] which is often Conduit/Root
                                    StructPropertyData template = null;
                                    foreach (var cand in nodesArr.Value)
                                    {
                                        if (cand is StructPropertyData spd)
                                        {
                                            var nm = spd.Value.OfType<NamePropertyData>().FirstOrDefault(p=>p.Name.Value.ToString()=="m_Name");
                                            var nameStr = nm?.Value.ToString() ?? "";
                                            if (!nameStr.Contains("Conduit", StringComparison.OrdinalIgnoreCase) && !nameStr.Contains("Root", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(nameStr) && nameStr!="None")
                                            { template = spd; break; }
                                        }
                                    }
                                    if (template == null) template = nodesArr.Value[0] as StructPropertyData;
                                    var newList = new List<PropertyData>(nodesArr.Value);
                                    for (int ni = 0; ni < newNodes.Count; ni++)
                                    {
                                        var cn = newNodes[ni];
                                        int newIdx = baseCount + ni;
                                        idToTreeIndex[cn.Id] = newIdx;
                                        var cloneDbPath = _customClones != null && _customClones.TryGetValue(cn.Id, out var cp) ? cp : null;
                                        if (cloneDbPath == null && _animToDbPath.TryGetValue(cn.AnimPath, out var existingDb)) cloneDbPath = existingDb;
                                        string attackName2 = cloneDbPath != null ? Path.GetFileNameWithoutExtension(cloneDbPath) : Path.GetFileNameWithoutExtension(cn.AnimPath);
                                        string attackPath2 = cloneDbPath != null ? EnsureLeadingSlash(cloneDbPath) : $"/Game/DB/_MainChar/Combos/Attacks/Custom/MainChar_Custom_{cn.Id}";
                                        int impIdx = FindImport(asset, attackName2, attackPath2);
                                        if (impIdx < 0) impIdx = AddAttackDBImport(asset, attackName2, attackPath2);
                                        // shallow clone template struct
                                        StructPropertyData newNodeStruct;
                                        if (template != null)
                                        {
                                            newNodeStruct = new StructPropertyData();
                                            newNodeStruct.Name = new FName(asset, "m_Nodes");
                                            newNodeStruct.StructType = template.StructType;
                                            newNodeStruct.Value = new List<PropertyData>();
                                            // deep copy children — replace patched ones with new instances to avoid mutating template
                                            for (int ci=0; ci<template.Value.Count; ci++)
                                            {
                                                var child = template.Value[ci];
                                                if (child.Name.Value.ToString()=="m_Name" && child is NamePropertyData np)
                                                {
                                                    newNodeStruct.Value.Add(new NamePropertyData{ Name=np.Name, Value=new FName(asset, $"Custom_{cn.Id}") });
                                                }
                                                else if (child.Name.Value.ToString()=="m_AttackInfos" && child is StructPropertyData sp)
                                                {
                                                    var spCopy = new StructPropertyData{ Name=sp.Name, StructType=sp.StructType, Value=new List<PropertyData>() };
                                                    foreach (var gc in sp.Value)
                                                    {
                                                        if (gc is MapPropertyData mp && gc.Name.Value.ToString()=="m_Attacks")
                                                        {
                                                            var mpCopy = new MapPropertyData{ Name=mp.Name, KeyType=mp.KeyType, ValueType=mp.ValueType, Value=new TMap<PropertyData,PropertyData>() };
                                                            // copy existing entries then overwrite first value to new import
                                                            bool first = true;
                                                            foreach (var kv in mp.Value)
                                                            {
                                                                if (first && kv.Value is ObjectPropertyData opd)
                                                                {
                                                                    mpCopy.Value.Add(kv.Key, new ObjectPropertyData{ Name=opd.Name, Value=FPackageIndex.FromImport(impIdx)});
                                                                    first = false;
                                                                }
                                                                else mpCopy.Value.Add(kv.Key, kv.Value);
                                                            }
                                                            if (mpCopy.Value.Count==0)
                                                                mpCopy.Value.Add(new NamePropertyData{ Name=new FName(asset,"m_Attacks"), Value=new FName(asset, attackPath2)}, new ObjectPropertyData{ Name=new FName(asset,"m_Attacks"), Value=FPackageIndex.FromImport(impIdx)});
                                                            spCopy.Value.Add(mpCopy);
                                                        }
                                                        else spCopy.Value.Add(gc);
                                                    }
                                                    newNodeStruct.Value.Add(spCopy);
                                                }
                                                else
                                                {
                                                    newNodeStruct.Value.Add(child);
                                                }
                                            }
                                        }
                                        else
                                        {
                                            newNodeStruct = template;
                                        }
                                        newList.Add(newNodeStruct);
                                        ErrorLog.Write("EXPORT", new Exception($"  APPENDED new node {cn.DisplayName} -> idx {newIdx} import {impIdx}"));
                                    }
                                    nodesArr.Value = newList.ToArray();
                                    // Inject edges: for each ComboEdge, ensure source m_Transitions has target
                                    foreach (var edge in _graph.Edges)
                                    {
                                        if (!idToTreeIndex.TryGetValue(edge.FromNodeId, out var srcIdx)) continue;
                                        if (!idToTreeIndex.TryGetValue(edge.ToNodeId, out var dstIdx)) continue;
                                        var srcNodeData = nodesArr.Value[srcIdx] as StructPropertyData;
                                        if (srcNodeData == null) continue;
                                        var transStruct = srcNodeData.Value.OfType<StructPropertyData>().FirstOrDefault(p=>p.Name.Value.ToString()=="m_Transitions");
                                        if (transStruct == null) continue;
                                        var transArr = transStruct.Value.OfType<ArrayPropertyData>().FirstOrDefault(p=>p.Name.Value.ToString()=="m_Transitions");
                                        if (transArr == null) continue;
                                        string inputEnum = edge.InputName?.Replace(" Delay","") ?? "";
                                        string ueEnum = inputEnum switch { "LMB"=>"Light","RMB"=>"Heavy","RMB Hold"=>"HeavyHold","S"=>"Special","Shift"=>"Dodge","Q"=>"Throw", _=>"Light"};
                                        // find existing transition element for this input
                                        StructPropertyData targetTrans = null;
                                        foreach (var elem in transArr.Value)
                                        {
                                            if (elem is StructPropertyData spd)
                                            {
                                                var enumProp = spd.Value.OfType<EnumPropertyData>().FirstOrDefault(p=>p.Name.Value.ToString()=="m_eInputTransition");
                                                if (enumProp != null && enumProp.Value.ToString().Contains(ueEnum)) { targetTrans = spd; break; }
                                            }
                                        }
                                        if (targetTrans == null)
                                        {
                                            // create new transition element by cloning first
                                            if (transArr.Value.Length>0 && transArr.Value[0] is StructPropertyData first)
                                            {
                                                targetTrans = new StructPropertyData { Name = first.Name, StructType = first.StructType, Value = new List<PropertyData>() };
                                                foreach (var ch in first.Value)
                                                {
                                                    if (ch.Name.Value.ToString()=="m_eInputTransition" && ch is EnumPropertyData ep)
                                                        targetTrans.Value.Add(new EnumPropertyData{ Name=ep.Name, EnumType=ep.EnumType, Value=new FName(asset, $"EComboInputTransition::{ueEnum}")});
                                                    else if (ch.Name.Value.ToString()=="m_TargetNodes" && ch is MapPropertyData mp)
                                                        targetTrans.Value.Add(new MapPropertyData{ Name=mp.Name, KeyType=mp.KeyType, ValueType=mp.ValueType, Value=new TMap<PropertyData,PropertyData>()});
                                                    else targetTrans.Value.Add(ch);
                                                }
                                                var list = new List<PropertyData>(transArr.Value) { targetTrans };
                                                transArr.Value = list.ToArray();
                                            }
                                        }
                                        if (targetTrans != null)
                                        {
                                            var map2 = targetTrans.Value.OfType<MapPropertyData>().FirstOrDefault(p=>p.Name.Value.ToString()=="m_TargetNodes");
                                            if (map2 != null && !map2.Value.Any(kvp=> kvp.Key is IntPropertyData ip && ip.Value==dstIdx))
                                            {
                                                map2.Value.Add(new IntPropertyData{ Name=new FName(asset,"m_TargetNodes"), Value=dstIdx }, new IntPropertyData{ Name=new FName(asset,"m_TargetNodes"), Value=1});
                                            }
                                        }
                                    }
                                    ErrorLog.Write("EXPORT", new Exception($"  Appended {newNodes.Count} custom nodes, injected edges, new total {nodesArr.Value.Length}"));
                                }
                            }
                        } catch (Exception ex2) { ErrorLog.Write("EXPORT", new Exception($"  APPEND/EDGE inject failed: {ex2.Message}")); }
                    }

                    ErrorLog.Write("EXPORT", new Exception($"Patched {patched}/{_modifiedNodes.Count} nodes (direct: {patched - patchedFallback}, fallback DB: {patchedFallback}, skipped empty DB: {skippedEmpty}, skipped no anim->db: {skippedNoDb})"));

                    // Phase 2: Swap combo tree Import entries for modified nodes
                    // This matches the working Fire Disciple mod approach: modify the combo tree's
                    // Import table to point to different DBs, rather than patching DB files.
                    // The .uexp (combo tree structure/transitions) stays untouched.
                    int comboSwaps = 0;
                    {
                        foreach (var node in _modifiedNodes)
                        {
                            if (string.IsNullOrEmpty(node.DefaultDBPath) || string.IsNullOrEmpty(node.AnimPath))
                                continue;

                            string oldDbShortName = Path.GetFileNameWithoutExtension(node.DefaultDBPath);
                            string newDbPath = string.IsNullOrEmpty(node.SourceDBPath) ? "" : node.SourceDBPath;
                            if (string.IsNullOrEmpty(newDbPath)) continue;
                            string newDbShortName = Path.GetFileNameWithoutExtension(newDbPath);
                            string newDbFullPath = "/" + newDbPath.TrimStart('/');

                            if (oldDbShortName == newDbShortName) continue;

                            foreach (var attacksMap in allMaps)
                            {
                                foreach (var kvp in attacksMap.Value)
                                {
                                    string keyStr = GetKeyString(kvp.Key);
                                    string normalizedDefault = NormalizeSlotKey(node.DefaultDBPath);
                                    if (keyStr != normalizedDefault) continue;

                                    var valData = kvp.Value as ObjectPropertyData;
                                    if (valData?.Value == null) continue;

                                    int curImportIdx = -(valData.Value.Index + 1);
                                    if (curImportIdx < 0 || curImportIdx >= asset.Imports.Count) continue;

                                    var curImport = asset.Imports[curImportIdx];
                                    string curDbName = curImport.ObjectName?.Value?.ToString() ?? "";

                                    int pkgOuterIdx = -(curImport.OuterIndex.Index + 1);
                                    string curPkgPath = "";
                                    if (pkgOuterIdx >= 0 && pkgOuterIdx < asset.Imports.Count)
                                        curPkgPath = asset.Imports[pkgOuterIdx].ObjectName?.Value?.ToString() ?? "";

                                    if (!curPkgPath.Contains(oldDbShortName, StringComparison.OrdinalIgnoreCase) &&
                                        curDbName != oldDbShortName)
                                    {
                                        ErrorLog.Write("EXPORT", new Exception($"  COMBO SKIP: {node.DisplayName} import[{curImportIdx}] name='{curDbName}' pkg='{curPkgPath}' != expected '{oldDbShortName}'"));
                                        break;
                                    }

                                    curImport.ObjectName = FName.FromString(asset, newDbShortName);

                                    if (pkgOuterIdx >= 0 && pkgOuterIdx < asset.Imports.Count)
                                        asset.Imports[pkgOuterIdx].ObjectName = FName.FromString(asset, newDbFullPath);

                                    comboSwaps++;
                                    ErrorLog.Write("EXPORT", new Exception($"  COMBO IMPORT SWAP: {oldDbShortName} -> {newDbShortName} (import[{curImportIdx}])"));
                                    break;
                                }
                            }
                        }
                    }

                    ErrorLog.Write("EXPORT", new Exception($"Combo tree swaps: {comboSwaps}"));

                    comboTreeModified = comboSwaps > 0;
                    if (comboTreeModified)
                    {
                        asset.Write(outUasset);

                        var comboVanillaUexp = Path.Combine(gameRoot, "DB", "_MainChar", "Combos", "MainChar_ComboTree.uexp");
                        var comboOutUexp = Path.ChangeExtension(outUasset, ".uexp");
                        if (File.Exists(comboVanillaUexp))
                            File.Copy(comboVanillaUexp, comboOutUexp, true);
                    }
                });

                UpdateStep(2, "done");
                SetProgress(50);

                if (comboTreeModified)
                {
                    fileEntries.Add((outUasset, "../../../Sifu/Content/DB/_MainChar/Combos/MainChar_ComboTree.uasset"));
                    fileEntries.Add((Path.ChangeExtension(outUasset, ".uexp"),
                        "../../../Sifu/Content/DB/_MainChar/Combos/MainChar_ComboTree.uexp"));
                }

                if (!string.IsNullOrEmpty(_enemyComboPath))
                {
                    var enemyComboRelPath = _enemyComboPath.Replace("Game/", "");
                    var enemyVanillaPath = Path.Combine(gameRoot, enemyComboRelPath + ".uasset");
                    if (File.Exists(enemyVanillaPath))
                    {
                        var enemyOutDir = Path.Combine(outputPath, "Sifu", "Content", Path.GetDirectoryName(enemyComboRelPath)!);
                        Directory.CreateDirectory(enemyOutDir);
                        var enemyOutUasset = Path.Combine(enemyOutDir, Path.GetFileName(enemyComboRelPath) + ".uasset");

                        await System.Threading.Tasks.Task.Run(() =>
                        {
                            var eng = EngineVersion.VER_UE4_26;
                            var enemyAsset = new UAsset(enemyVanillaPath, eng, null, CustomSerializationFlags.None);

                            NormalExport? enemyComboExport = null;
                            for (int i = 0; i < enemyAsset.Exports.Count; i++)
                            {
                                if (enemyAsset.Exports[i] is NormalExport ne && ne.SerialSize > 1000)
                                {
                                    enemyComboExport = ne;
                                    break;
                                }
                            }

                            if (enemyComboExport == null)
                            {
                                ErrorLog.Write("EXPORT", new Exception($"Could not find enemy Combo export in {enemyVanillaPath}"));
                                return;
                            }

                            ErrorLog.Write("EXPORT", new Exception($"Loaded enemy combo tree: {enemyVanillaPath}"));

                            var enemyNodesArr = enemyComboExport.Data.OfType<ArrayPropertyData>()
                                .FirstOrDefault(p => p.Name.Value.ToString() == "m_Nodes");
                            if (enemyNodesArr == null || enemyNodesArr.Value == null || enemyNodesArr.Value.Length == 0)
                            {
                                ErrorLog.Write("EXPORT", new Exception("Enemy combo tree has no m_Nodes array"));
                                return;
                            }

                            var enemyComboBinSwaps = new List<(string oldShortName, string newShortName, string newFullPath)>();
                            {
                                var enemyAllMaps = FindAllMapsNamed(enemyComboExport.Data, "m_Attacks");
                                ErrorLog.Write("EXPORT", new Exception($"Enemy combo tree: found {enemyAllMaps.Count} m_Attacks maps, {enemyAsset.Imports.Count} imports"));

                                var enemyModifiedNodes = _modifiedNodes
                                    .Where(n => !string.IsNullOrEmpty(n.DefaultDBPath)
                                        && n.DefaultDBPath.Contains("/AI/Archetypes/", StringComparison.OrdinalIgnoreCase)
                                        && n.TreeIndex >= 0)
                                    .ToList();
                                ErrorLog.Write("EXPORT", new Exception($"Enemy combo Import swap: {enemyModifiedNodes.Count} modified existing nodes"));

                                foreach (var node in enemyModifiedNodes)
                                {
                                    string oldDbShortName = Path.GetFileNameWithoutExtension(node.DefaultDBPath);
                                    string newDbPath = string.IsNullOrEmpty(node.SourceDBPath) ? "" : node.SourceDBPath;
                                    if (string.IsNullOrEmpty(newDbPath)) continue;
                                    string newDbShortName = Path.GetFileNameWithoutExtension(newDbPath);
                                    string newDbFullPath = "/" + newDbPath.TrimStart('/');

                                    if (oldDbShortName == newDbShortName) continue;

                                    bool swapped = false;
                                    foreach (var attacksMap in enemyAllMaps)
                                    {
                                        foreach (var kvp in attacksMap.Value)
                                        {
                                            string keyStr = GetKeyString(kvp.Key);
                                            string normalizedDefault = NormalizeSlotKey(node.DefaultDBPath);
                                            if (keyStr != normalizedDefault) continue;

                                            var valData = kvp.Value as ObjectPropertyData;
                                            if (valData?.Value == null) continue;

                                            int curImportIdx = -(valData.Value.Index + 1);
                                            if (curImportIdx < 0 || curImportIdx >= enemyAsset.Imports.Count) continue;

                                            var curImport = enemyAsset.Imports[curImportIdx];
                                            string curDbName = curImport.ObjectName?.Value?.ToString() ?? "";

                                            int pkgOuterIdx = -(curImport.OuterIndex.Index + 1);
                                            string curPkgPath = "";
                                            if (pkgOuterIdx >= 0 && pkgOuterIdx < enemyAsset.Imports.Count)
                                                curPkgPath = enemyAsset.Imports[pkgOuterIdx].ObjectName?.Value?.ToString() ?? "";

                                            if (!curPkgPath.Contains(oldDbShortName, StringComparison.OrdinalIgnoreCase) &&
                                                curDbName != oldDbShortName)
                                            {
                                                ErrorLog.Write("EXPORT", new Exception($"  ENEMY COMBO SKIP: {node.DisplayName} import[{curImportIdx}] name='{curDbName}' pkg='{curPkgPath}' != expected '{oldDbShortName}'"));
                                                break;
                                            }

                                            enemyComboBinSwaps.Add((oldDbShortName, newDbShortName, newDbFullPath));
                                            swapped = true;
                                            ErrorLog.Write("EXPORT", new Exception($"  ENEMY COMBO IMPORT SWAP: {oldDbShortName} -> {newDbShortName} (import[{curImportIdx}])"));
                                            break;
                                        }
                                        if (swapped) break;
                                    }
                                }
                            }

                            ErrorLog.Write("EXPORT", new Exception($"Enemy combo tree swaps: {enemyComboBinSwaps.Count}"));

                            if (enemyComboBinSwaps.Count > 0)
                            {
                                try
                                {
                                    var processedSwaps = new HashSet<string>();
                                    foreach (var (oldShortName, newShortName, newFullPath) in enemyComboBinSwaps)
                                    {
                                        for (int i = 0; i < enemyAsset.Imports.Count; i++)
                                        {
                                            var imp = enemyAsset.Imports[i];
                                            string objName = imp.ObjectName?.Value?.ToString() ?? "";

                                            if (objName == oldShortName)
                                            {
                                                int outerIdx = -(imp.OuterIndex.Index + 1);

                                                if (!processedSwaps.Contains(oldShortName))
                                                {
                                                    imp.ObjectName = FName.FromString(enemyAsset, newShortName);
                                                    ErrorLog.Write("EXPORT", new Exception($"  UAPI SWAP import[{i}]: {oldShortName} -> {newShortName}"));
                                                }

                                                if (outerIdx >= 0 && outerIdx < enemyAsset.Imports.Count && !processedSwaps.Contains(newFullPath))
                                                {
                                                    enemyAsset.Imports[outerIdx].ObjectName = FName.FromString(enemyAsset, newFullPath);
                                                    ErrorLog.Write("EXPORT", new Exception($"  UAPI SWAP outer[{outerIdx}]: -> {newFullPath}"));
                                                }

                                                processedSwaps.Add(oldShortName);
                                                processedSwaps.Add(newFullPath);
                                                break;
                                            }
                                        }
                                    }

                                    var swapDict = enemyComboBinSwaps.ToDictionary(
                                        s => s.oldShortName,
                                        s => (s.newShortName, s.newFullPath));

                                    int fNamesPatched = 0;
                                    foreach (var elem in enemyNodesArr.Value)
                                    {
                                        if (elem is not StructPropertyData nodeStruct || nodeStruct.Value == null) continue;

                                        var attackInfos = nodeStruct.Value
                                            .OfType<StructPropertyData>()
                                            .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_AttackInfos");
                                        if (attackInfos?.Value == null) continue;

                                        foreach (var field in attackInfos.Value)
                                        {
                                            if (field is not NamePropertyData nameProp) continue;
                                            if (nameProp.Name?.Value?.ToString() != "m_Attacks") continue;

                                            string curPath = nameProp.Value?.Value?.ToString() ?? "";
                                            if (string.IsNullOrEmpty(curPath) || curPath == "None") continue;

                                            string curShortName = Path.GetFileNameWithoutExtension(curPath);
                                            if (!swapDict.TryGetValue(curShortName, out var swap)) continue;

                                            string newFNamePath = swap.newFullPath + "." + swap.newShortName;
                                            nameProp.Value = FName.FromString(enemyAsset, newFNamePath);
                                            fNamesPatched++;
                                        }
                                    }
                                    ErrorLog.Write("EXPORT", new Exception($"  UAPI: patched {fNamesPatched} FName paths in m_Nodes"));

                                    var mAttacksMap = enemyComboExport.Data
                                        .OfType<MapPropertyData>()
                                        .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_Attacks");
                                    if (mAttacksMap?.Value != null)
                                    {
                                        var rebuilt = new TMap<PropertyData, PropertyData>();
                                        foreach (var kvp in mAttacksMap.Value)
                                        {
                                            string keyStr = GetKeyString(kvp.Key);
                                            string shortKey = Path.GetFileNameWithoutExtension(keyStr);
                                            if (swapDict.TryGetValue(shortKey, out var mapSwap))
                                            {
                                                string newFNamePath = mapSwap.newFullPath + "." + mapSwap.newShortName;
                                                var newKeyNameProp = new NamePropertyData
                                                {
                                                    Name = new FName(enemyAsset, "m_Attacks"),
                                                    Value = FName.FromString(enemyAsset, newFNamePath)
                                                };
                                                rebuilt.Add(newKeyNameProp, kvp.Value);
                                                ErrorLog.Write("EXPORT", new Exception($"  UAPI MAP KEY: {shortKey} -> {newFNamePath}"));
                                            }
                                            else
                                            {
                                                rebuilt.Add(kvp.Key, kvp.Value);
                                            }
                                        }
                                        mAttacksMap.Value = rebuilt;
                                    }
                                }
                                catch (Exception ex)
                                {
                                    ErrorLog.Write("EXPORT", new Exception($"  UAPI SWAP FAIL: {ex.Message}\n{ex.StackTrace}"));
                                }
                            }

                            try
                            {
                                if (_graph != null)
                                {
                                        var newNodes = _graph.Nodes.Where(n => n.TreeIndex == -1).ToList();
                                        if (newNodes.Count > 0)
                                        {
                                            var nodesArr = enemyComboExport.Data.OfType<ArrayPropertyData>()
                                                .FirstOrDefault(p => p.Name.Value.ToString() == "m_Nodes");
                                            if (nodesArr != null && nodesArr.Value != null && nodesArr.Value.Length > 0)
                                            {
                                                var idToTreeIndex = new Dictionary<int, int>();
                                                foreach (var n in _graph.Nodes.Where(n => n.TreeIndex >= 0))
                                                    idToTreeIndex[n.Id] = n.TreeIndex;
                                                int baseCount = nodesArr.Value.Length;

                                                StructPropertyData template = null;
                                                foreach (var cand in nodesArr.Value)
                                                {
                                                    if (cand is StructPropertyData spd)
                                                    {
                                                        var nm = spd.Value.OfType<NamePropertyData>().FirstOrDefault(p => p.Name.Value.ToString() == "m_Name");
                                                        var nameStr = nm?.Value.ToString() ?? "";
                                                        if (!nameStr.Contains("Conduit", StringComparison.OrdinalIgnoreCase) && !nameStr.Contains("Root", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(nameStr) && nameStr != "None")
                                                        { template = spd; break; }
                                                    }
                                                }
                                                if (template == null) template = nodesArr.Value[0] as StructPropertyData;

                                                var attackNodes = newNodes.Where(n => !n.IsRedirect).ToList();
                                                var redirectNodes = newNodes.Where(n => n.IsRedirect).ToList();

                                                var allNewNodes = new List<ComboNode>();
                                                allNewNodes.AddRange(attackNodes);
                                                allNewNodes.AddRange(redirectNodes);

                                                var newList = new List<PropertyData>(nodesArr.Value);
                                                for (int ni = 0; ni < allNewNodes.Count; ni++)
                                                {
                                                    var cn = allNewNodes[ni];
                                                    int newIdx = baseCount + ni;
                                                    idToTreeIndex[cn.Id] = newIdx;
                                                    var cloneDbPath = _customClones != null && _customClones.TryGetValue(cn.Id, out var cp) ? cp : null;
                                                    if (cloneDbPath == null && _animToDbPath.TryGetValue(cn.AnimPath, out var existingDb)) cloneDbPath = existingDb;
                                                    string attackName2 = cloneDbPath != null ? Path.GetFileNameWithoutExtension(cloneDbPath) : Path.GetFileNameWithoutExtension(cn.AnimPath);
                                                    if (string.IsNullOrEmpty(attackName2)) attackName2 = "Custom_" + cn.Id;
                                                    string attackPath2 = cloneDbPath != null ? EnsureLeadingSlash(cloneDbPath) : $"/Game/DB/_MainChar/Combos/Attacks/Custom/MainChar_Custom_{cn.Id}";
                                                    int impIdx = FindImport(enemyAsset, attackName2, attackPath2);
                                                    if (impIdx < 0) impIdx = AddAttackDBImport(enemyAsset, attackName2, attackPath2);

                                                    StructPropertyData newNodeStruct = null;
                                                    if (template != null)
                                                    {
                                                        newNodeStruct = new StructPropertyData();
                                                        newNodeStruct.Name = new FName(enemyAsset, "m_Nodes");
                                                        newNodeStruct.StructType = template.StructType;
                                                        newNodeStruct.Value = new List<PropertyData>();
                                                        for (int ci = 0; ci < template.Value.Count; ci++)
                                                        {
                                                            var child = template.Value[ci];
                                                            if (child.Name.Value.ToString() == "m_Name" && child is NamePropertyData np)
                                                            {
                                                                string nodeName = cn.IsRedirect ? $"Redirect_{cn.Id}" : $"Custom_{cn.Id}";
                                                                newNodeStruct.Value.Add(new NamePropertyData { Name = np.Name, Value = new FName(enemyAsset, nodeName) });
                                                            }
                                                            else if (child.Name.Value.ToString() == "m_NodeRedirect" && child is IntPropertyData)
                                                            {
                                                                int redirectTarget = -1;
                                                                if (cn.IsRedirect && cn.RedirectTargetId >= 0)
                                                                {
                                                                    var targetNode = _graph.Nodes.FirstOrDefault(n => n.Id == cn.RedirectTargetId);
                                                                    if (targetNode != null && idToTreeIndex.TryGetValue(targetNode.Id, out var resolved))
                                                                        redirectTarget = resolved;
                                                                    else
                                                                        redirectTarget = cn.RedirectTargetId;
                                                                }
                                                                newNodeStruct.Value.Add(new IntPropertyData { Name = new FName(enemyAsset, "m_NodeRedirect"), Value = redirectTarget });
                                                            }
                                                            else if (child.Name.Value.ToString() == "m_AttackInfos" && child is StructPropertyData sp)
                                                            {
                                                                var spCopy = new StructPropertyData { Name = sp.Name, StructType = sp.StructType, Value = new List<PropertyData>() };
                                                                string fAttackPath = attackPath2 + "." + attackName2;
                                                                foreach (var gc in sp.Value)
                                                                {
                                                                    if (cn.IsRedirect)
                                                                    {
                                                                        if (gc.Name.Value.ToString() == "m_Attacks" && gc is NamePropertyData)
                                                                            spCopy.Value.Add(new NamePropertyData { Name = gc.Name, Value = FName.FromString(enemyAsset, "None") });
                                                                        else spCopy.Value.Add(gc);
                                                                    }
                                                                    else
                                                                    {
                                                                        if (gc.Name.Value.ToString() == "m_Attacks" && gc is NamePropertyData)
                                                                            spCopy.Value.Add(new NamePropertyData { Name = gc.Name, Value = FName.FromString(enemyAsset, fAttackPath) });
                                                                        else spCopy.Value.Add(gc);
                                                                    }
                                                                }
                                                                newNodeStruct.Value.Add(spCopy);
                                                            }
                                                            else
                                                            {
                                                                newNodeStruct.Value.Add(child);
                                                            }
                                                        }
                                                    }
                                                    else
                                                    {
                                                        newNodeStruct = template;
                                                    }
                                                    newList.Add(newNodeStruct);
                                                    ErrorLog.Write("EXPORT", new Exception($"  ENEMY APPEND: node {cn.DisplayName} -> idx {newIdx} redirect={cn.IsRedirect} imp={impIdx}"));
                                                }
                                                nodesArr.Value = newList.ToArray();

                                                var rootAttacksMap = enemyComboExport.Data
                                                    .OfType<MapPropertyData>()
                                                    .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_Attacks");
                                                if (rootAttacksMap?.Value != null)
                                                {
                                                    foreach (var cn2 in attackNodes)
                                                    {
                                                        var cloneDbPath2 = _customClones != null && _customClones.TryGetValue(cn2.Id, out var cp2) ? cp2 : null;
                                                        if (cloneDbPath2 == null && _animToDbPath.TryGetValue(cn2.AnimPath, out var existingDb2)) cloneDbPath2 = existingDb2;
                                                        string aName2 = cloneDbPath2 != null ? Path.GetFileNameWithoutExtension(cloneDbPath2) : Path.GetFileNameWithoutExtension(cn2.AnimPath);
                                                        string aPath2 = cloneDbPath2 != null ? EnsureLeadingSlash(cloneDbPath2) : $"/Game/DB/_MainChar/Combos/Attacks/Custom/MainChar_Custom_{cn2.Id}";
                                                        if (string.IsNullOrEmpty(aName2)) aName2 = "Custom_" + cn2.Id;
                                                        string fAttackPath2 = aPath2 + "." + aName2;
                                                        bool found = false;
                                                        foreach (var kv in rootAttacksMap.Value)
                                                        {
                                                            if (GetKeyString(kv.Key) == fAttackPath2) { found = true; break; }
                                                        }
                                                        if (!found)
                                                        {
                                                            int imp2 = FindImport(enemyAsset, aName2, aPath2);
                                                            if (imp2 < 0) imp2 = AddAttackDBImport(enemyAsset, aName2, aPath2);
                                                            rootAttacksMap.Value.Add(
                                                                new NamePropertyData { Name = new FName(enemyAsset, "m_Attacks"), Value = FName.FromString(enemyAsset, fAttackPath2) },
                                                                new ObjectPropertyData { Name = new FName(enemyAsset, "m_Attacks"), Value = FPackageIndex.FromImport(imp2) }
                                                            );
                                                            ErrorLog.Write("EXPORT", new Exception($"  ENEMY ROOT MAP: added {fAttackPath2} -> imp[{imp2}]"));
                                                        }
                                                    }
                                                }

                                                var newIds = new HashSet<int>(allNewNodes.Select(n => n.Id));
                                                foreach (var edge in _graph.Edges)
                                                {
                                                    if (!newIds.Contains(edge.FromNodeId) && !newIds.Contains(edge.ToNodeId)) continue;
                                                    if (!idToTreeIndex.TryGetValue(edge.FromNodeId, out var srcIdx)) continue;
                                                    if (!idToTreeIndex.TryGetValue(edge.ToNodeId, out var dstIdx)) continue;
                                                    var srcNodeData = nodesArr.Value[srcIdx] as StructPropertyData;
                                                    if (srcNodeData == null) continue;
                                                    var transStruct = srcNodeData.Value.OfType<StructPropertyData>().FirstOrDefault(p => p.Name.Value.ToString() == "m_Transitions");
                                                    if (transStruct == null) continue;
                                                    var transArr = transStruct.Value.OfType<ArrayPropertyData>().FirstOrDefault(p => p.Name.Value.ToString() == "m_Transitions");
                                                    if (transArr == null) continue;
                                                    string inputEnum = edge.InputName?.Replace(" Delay", "") ?? "";
                                                    string ueEnum = inputEnum switch { "LMB" => "Light", "RMB" => "Heavy", "RMB Hold" => "HeavyHold", "S" => "Special", "Shift" => "Dodge", "Q" => "Throw", _ => "Light" };
                                                    StructPropertyData targetTrans = null;
                                                    foreach (var elem in transArr.Value)
                                                    {
                                                        if (elem is StructPropertyData spd)
                                                        {
                                                            var enumProp = spd.Value.OfType<EnumPropertyData>().FirstOrDefault(p => p.Name.Value.ToString() == "m_eInputTransition");
                                                            if (enumProp != null && enumProp.Value.ToString().Contains(ueEnum)) { targetTrans = spd; break; }
                                                        }
                                                    }
                                                    if (targetTrans == null)
                                                    {
                                                        if (transArr.Value.Length > 0 && transArr.Value[0] is StructPropertyData first)
                                                        {
                                                            targetTrans = new StructPropertyData { Name = first.Name, StructType = first.StructType, Value = new List<PropertyData>() };
                                                            foreach (var ch in first.Value)
                                                            {
                                                                if (ch.Name.Value.ToString() == "m_eInputTransition" && ch is EnumPropertyData ep)
                                                                    targetTrans.Value.Add(new EnumPropertyData { Name = ep.Name, EnumType = ep.EnumType, Value = new FName(enemyAsset, $"EComboInputTransition::{ueEnum}") });
                                                                else if (ch.Name.Value.ToString() == "m_TargetNodes" && ch is MapPropertyData mp)
                                                                    targetTrans.Value.Add(new MapPropertyData { Name = mp.Name, KeyType = mp.KeyType, ValueType = mp.ValueType, Value = new TMap<PropertyData, PropertyData>() });
                                                                else targetTrans.Value.Add(ch);
                                                            }
                                                            var list = new List<PropertyData>(transArr.Value) { targetTrans };
                                                            transArr.Value = list.ToArray();
                                                        }
                                                    }
                                                    if (targetTrans != null)
                                                    {
                                                        var map2 = targetTrans.Value.OfType<MapPropertyData>().FirstOrDefault(p => p.Name.Value.ToString() == "m_TargetNodes");
                                                        if (map2 != null && !map2.Value.Any(kvp => kvp.Key is IntPropertyData ip && ip.Value == dstIdx))
                                                        {
                                                            map2.Value.Add(new IntPropertyData { Name = new FName(enemyAsset, "m_TargetNodes"), Value = dstIdx }, new IntPropertyData { Name = new FName(enemyAsset, "m_TargetNodes"), Value = 1 });
                                                        }
                                                    }
                                                }
                                                ErrorLog.Write("EXPORT", new Exception($"  ENEMY APPEND: {newNodes.Count} nodes ({attackNodes.Count} attack + {redirectNodes.Count} redirect), edges {_graph.Edges.Count}, total {nodesArr.Value.Length}"));
                                            }
                                        }
                                }

                                Directory.CreateDirectory(Path.GetDirectoryName(enemyOutUasset)!);
                                enemyAsset.Write(enemyOutUasset);
                                ErrorLog.Write("EXPORT", new Exception($"  UAPI: wrote {new FileInfo(enemyOutUasset).Length} bytes .uasset"));
                                var outUexpPath = Path.ChangeExtension(enemyOutUasset, ".uexp");
                                if (File.Exists(outUexpPath))
                                    ErrorLog.Write("EXPORT", new Exception($"  UAPI: wrote {new FileInfo(outUexpPath).Length} bytes .uexp"));
                            }
                            catch (Exception ex)
                            {
                                ErrorLog.Write("EXPORT", new Exception($"  UAPI FAIL: {ex.Message}\n{ex.StackTrace}"));
                            }

                            // === Arena combo patching ===
                            // Find the character root from the story combo path
                            // e.g. "DB/AI/Archetypes/Yang/_DB/Phase1/Yang_P1_Combo" -> "DB/AI/Archetypes/Yang"
                        var comboPathParts = enemyComboRelPath.Replace('\\', '/').Split('/');
                        int archetypesIdx = Array.IndexOf(comboPathParts, "Archetypes");
                        if (archetypesIdx >= 0 && archetypesIdx + 1 < comboPathParts.Length)
                        {
                            string charRoot = string.Join("/", comboPathParts.Take(archetypesIdx + 2));
                            string charArenaDir = Path.Combine(gameRoot, charRoot, "_Arena");
                            if (Directory.Exists(charArenaDir))
                            {
                                var arenaComboFiles = Directory.GetFiles(charArenaDir, "*.uasset", SearchOption.AllDirectories)
                                    .Where(f => Path.GetFileNameWithoutExtension(f).Contains("Combo", StringComparison.OrdinalIgnoreCase))
                                    .ToList();

                                foreach (var arenaComboVanilla in arenaComboFiles)
                                {
                                    var arenaComboRelFromContent = Path.GetRelativePath(Path.Combine(gameRoot), arenaComboVanilla).Replace('\\', '/');
                                    if (arenaComboRelFromContent.EndsWith(".uasset"))
                                        arenaComboRelFromContent = arenaComboRelFromContent[..^7];

                                    try
                                    {
                                        var arenaEng = EngineVersion.VER_UE4_26;
                                        var arenaAsset = new UAsset(arenaComboVanilla, arenaEng, null, CustomSerializationFlags.None);

                                        NormalExport? arenaComboExport = null;
                                        for (int i = 0; i < arenaAsset.Exports.Count; i++)
                                        {
                                            if (arenaAsset.Exports[i] is NormalExport ne && ne.SerialSize > 1000)
                                            {
                                                arenaComboExport = ne;
                                                break;
                                            }
                                        }
                                        if (arenaComboExport == null)
                                        {
                                            ErrorLog.Write("EXPORT", new Exception($"  ARENA SKIP: no combo export in {Path.GetFileName(arenaComboVanilla)}"));
                                            continue;
                                        }

                                        var arenaNodesArr = arenaComboExport.Data.OfType<ArrayPropertyData>()
                                            .FirstOrDefault(p => p.Name.Value.ToString() == "m_Nodes");
                                        if (arenaNodesArr?.Value == null || arenaNodesArr.Value.Length == 0)
                                        {
                                            ErrorLog.Write("EXPORT", new Exception($"  ARENA SKIP: no m_Nodes in {Path.GetFileName(arenaComboVanilla)}"));
                                            continue;
                                        }

                                        var arenaSwapDict = enemyComboBinSwaps.ToDictionary(
                                            s => s.oldShortName,
                                            s => (s.newShortName, s.newFullPath));

                                        var arenaProcessedSwaps = new HashSet<string>();
                                        foreach (var (oldShortName, newShortName, newFullPath) in enemyComboBinSwaps)
                                        {
                                            for (int i = 0; i < arenaAsset.Imports.Count; i++)
                                            {
                                                var imp = arenaAsset.Imports[i];
                                                string objName = imp.ObjectName?.Value?.ToString() ?? "";
                                                if (objName == oldShortName)
                                                {
                                                    int outerIdx = -(imp.OuterIndex.Index + 1);
                                                    if (!arenaProcessedSwaps.Contains(oldShortName))
                                                    {
                                                        imp.ObjectName = FName.FromString(arenaAsset, newShortName);
                                                    }
                                                    if (outerIdx >= 0 && outerIdx < arenaAsset.Imports.Count && !arenaProcessedSwaps.Contains(newFullPath))
                                                    {
                                                        arenaAsset.Imports[outerIdx].ObjectName = FName.FromString(arenaAsset, newFullPath);
                                                    }
                                                    arenaProcessedSwaps.Add(oldShortName);
                                                    arenaProcessedSwaps.Add(newFullPath);
                                                    break;
                                                }
                                            }
                                        }

                                        int arenaFNamesPatched = 0;
                                        foreach (var elem in arenaNodesArr.Value)
                                        {
                                            if (elem is not StructPropertyData nodeStruct || nodeStruct.Value == null) continue;
                                            var attackInfos = nodeStruct.Value
                                                .OfType<StructPropertyData>()
                                                .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_AttackInfos");
                                            if (attackInfos?.Value == null) continue;
                                            foreach (var field in attackInfos.Value)
                                            {
                                                if (field is not NamePropertyData nameProp) continue;
                                                if (nameProp.Name?.Value?.ToString() != "m_Attacks") continue;
                                                string curPath = nameProp.Value?.Value?.ToString() ?? "";
                                                if (string.IsNullOrEmpty(curPath) || curPath == "None") continue;
                                                string curShortName = Path.GetFileNameWithoutExtension(curPath);
                                                if (!arenaSwapDict.TryGetValue(curShortName, out var swap)) continue;
                                                string newFNamePath = swap.newFullPath + "." + swap.newShortName;
                                                nameProp.Value = FName.FromString(arenaAsset, newFNamePath);
                                                arenaFNamesPatched++;
                                            }
                                        }

                                        var arenaMap = arenaComboExport.Data
                                            .OfType<MapPropertyData>()
                                            .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_Attacks");
                                        if (arenaMap?.Value != null)
                                        {
                                            var rebuilt = new TMap<PropertyData, PropertyData>();
                                            foreach (var kvp in arenaMap.Value)
                                            {
                                                string keyStr = GetKeyString(kvp.Key);
                                                string shortKey = Path.GetFileNameWithoutExtension(keyStr);
                                                if (arenaSwapDict.TryGetValue(shortKey, out var mapSwap))
                                                {
                                                    string newFNamePath = mapSwap.newFullPath + "." + mapSwap.newShortName;
                                                    var newKeyNameProp = new NamePropertyData
                                                    {
                                                        Name = new FName(arenaAsset, "m_Attacks"),
                                                        Value = FName.FromString(arenaAsset, newFNamePath)
                                                    };
                                                    rebuilt.Add(newKeyNameProp, kvp.Value);
                                                }
                                                else
                                                {
                                                    rebuilt.Add(kvp.Key, kvp.Value);
                                                }
                                            }
                                            arenaMap.Value = rebuilt;
                                        }

                                        var arenaOutDir = Path.Combine(outputPath, "Sifu", "Content", Path.GetDirectoryName(arenaComboRelFromContent)!);
                                        Directory.CreateDirectory(arenaOutDir);
                                        var arenaOutUasset = Path.Combine(arenaOutDir, Path.GetFileName(arenaComboRelFromContent) + ".uasset");
                                        arenaAsset.Write(arenaOutUasset);
                                        var arenaOutUexp = Path.ChangeExtension(arenaOutUasset, ".uexp");

                                        fileEntries.Add((arenaOutUasset,
                                            "../../../Sifu/Content/" + arenaComboRelFromContent + ".uasset"));
                                        fileEntries.Add((arenaOutUexp,
                                            "../../../Sifu/Content/" + arenaComboRelFromContent + ".uexp"));

                                        ErrorLog.Write("EXPORT", new Exception($"  ARENA: patched {Path.GetFileName(arenaComboVanilla)} ({new FileInfo(arenaOutUasset).Length} bytes .uasset, {arenaFNamesPatched} FNames, map keys rebuilt)"));
                                    }
                                    catch (Exception ex)
                                    {
                                        ErrorLog.Write("EXPORT", new Exception($"  ARENA FAIL: {Path.GetFileName(arenaComboVanilla)}: {ex.Message}"));
                                    }
                                }
                            }
                        }
                        });

                        if (File.Exists(enemyOutUasset))
                        {
                            fileEntries.Add((enemyOutUasset,
                                "../../../Sifu/Content/" + enemyComboRelPath + ".uasset"));
                            fileEntries.Add((Path.ChangeExtension(enemyOutUasset, ".uexp"),
                                "../../../Sifu/Content/" + enemyComboRelPath + ".uexp"));
                        }
                    }
                    else
                    {
                        ErrorLog.Write("EXPORT", new Exception($"Enemy combo file not found: {enemyVanillaPath}"));
                    }
                }
            }
            else
            {
                UpdateStep(1, "done");
                UpdateStep(2, "done");
                SetProgress(50);
            }

            // Phase 2: Stance DB files (only if stance changed) — load char-specific UAsset
            if (hasStanceChange)
            {

                // Patch BP_TransitionAnimRequest
                if (!string.IsNullOrEmpty(_charTransitionPath))
                {
                    txtCurrentAction.Text = "Patching BP_TransitionAnimRequest...";
                    var vanillaTransPath = Path.Combine(gameRoot, "DB/Movement/Transition/BP_TransitionAnimRequest.uasset");
                    var charTransPath = Path.Combine(gameRoot, _charTransitionPath + ".uasset");

                    if (File.Exists(vanillaTransPath) && File.Exists(charTransPath))
                    {
                        var outTransDir = Path.Combine(outputPath, "Sifu", "Content", "DB", "Movement", "Transition");
                        Directory.CreateDirectory(outTransDir);
                        var outTransAsset = Path.Combine(outTransDir, "BP_TransitionAnimRequest.uasset");

                        var eng = EngineVersion.VER_UE4_26;
                        await System.Threading.Tasks.Task.Run(() =>
                            PatchStanceAsset(vanillaTransPath, charTransPath, outTransAsset, eng, _referenceModDir, _activeStance ?? "", "BP_TransitionAnimRequest"));

                        fileEntries.Add((outTransAsset,
                            "../../../Sifu/Content/DB/Movement/Transition/BP_TransitionAnimRequest.uasset"));
                        fileEntries.Add((Path.ChangeExtension(outTransAsset, ".uexp"),
                            "../../../Sifu/Content/DB/Movement/Transition/BP_TransitionAnimRequest.uexp"));

                        ErrorLog.Write("EXPORT", new Exception($"Patched BP_TransitionAnimRequest for stance '{_activeStance}'"));
                    }
                    else
                    {
                        ErrorLog.Write("EXPORT", new Exception(
                            $"Transition patch skipped: vanilla={File.Exists(vanillaTransPath)}, char={File.Exists(charTransPath)}"));
                    }
                }

                // Patch BaseMovementDB
                if (!string.IsNullOrEmpty(_charBaseMovementDBPath))
                {
                    txtCurrentAction.Text = "Patching BaseMovementDB...";
                    var vanillaDbPath = Path.Combine(gameRoot, "DB/Movement/BaseMovementDB.uasset");
                    var charDbPath = Path.Combine(gameRoot, _charBaseMovementDBPath + ".uasset");

                    if (File.Exists(vanillaDbPath) && File.Exists(charDbPath))
                    {
                        var outDbDir = Path.Combine(outputPath, "Sifu", "Content", "DB", "Movement");
                        Directory.CreateDirectory(outDbDir);
                        var outDbAsset = Path.Combine(outDbDir, "BaseMovementDB.uasset");

                        var eng = EngineVersion.VER_UE4_26;
                        await System.Threading.Tasks.Task.Run(() =>
                            PatchStanceAsset(vanillaDbPath, charDbPath, outDbAsset, eng, _referenceModDir, _activeStance ?? "", "BaseMovementDB"));

                        fileEntries.Add((outDbAsset,
                            "../../../Sifu/Content/DB/Movement/BaseMovementDB.uasset"));
                        fileEntries.Add((Path.ChangeExtension(outDbAsset, ".uexp"),
                            "../../../Sifu/Content/DB/Movement/BaseMovementDB.uexp"));

                        ErrorLog.Write("EXPORT", new Exception($"Patched BaseMovementDB for stance '{_activeStance}'"));
                    }
                    else
                    {
                        ErrorLog.Write("EXPORT", new Exception(
                            $"BaseMovementDB patch skipped: vanilla={File.Exists(vanillaDbPath)}, char={File.Exists(charDbPath)}"));
                    }
                }
            }

            if (fileEntries.Count == 0)
            {
                ShowError("No files to export.");
                return;
            }

            ErrorLog.Write("EXPORT", new Exception($"Total files for pak: {fileEntries.Count}"));
            SetProgress(65);

            // Step 3: Build pak
            UpdateStep(3, "active");
            txtCurrentAction.Text = "Building pak file...";

            var tempDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "temp_mod");
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
            Directory.CreateDirectory(tempDir);

            foreach (var (src, dest) in fileEntries)
            {
                var tempDest = Path.Combine(tempDir, dest.TrimStart('/').Replace('/', '\\'));
                Directory.CreateDirectory(Path.GetDirectoryName(tempDest)!);
                if (File.Exists(src))
                    File.Copy(src, tempDest, true);
            }

            var filelistPath = Path.Combine(tempDir, "filelist.txt");
            var filelistContent = string.Join("\n", fileEntries.Select(f => $"\"{f.src}\" \"{f.dest}\""));
            File.WriteAllText(filelistPath, filelistContent);

            ErrorLog.Write("EXPORT", new Exception($"Filelist content:\n{filelistContent}"));

            var pakExe = @"C:\Users\Charles\Downloads\Sifu Modding\Unreal Pak Extracter and Creator\4.26\UE4\UnrealPak\UnrealPak.exe";
            var pakFileName = GetModFileName();
            _pakPath = Path.Combine(outputPath, pakFileName);

            if (!File.Exists(pakExe))
            {
                ShowError($"UnrealPak not found at:\n{pakExe}");
                return;
            }

            SetProgress(75);

            var pakArgs = $"\"{_pakPath}\" -create=\"{filelistPath}\"";

            var psi = new ProcessStartInfo
            {
                FileName = pakExe,
                Arguments = pakArgs,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var proc = Process.Start(psi);
            var stdout = await proc.StandardOutput.ReadToEndAsync();
            var stderr = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();

            ErrorLog.Write("EXPORT", new Exception($"UnrealPak stdout:\n{stdout}"));
            ErrorLog.Write("EXPORT", new Exception($"UnrealPak stderr:\n{stderr}"));
            ErrorLog.Write("EXPORT", new Exception($"UnrealPak exit code: {proc.ExitCode}"));

            if (proc.ExitCode != 0)
            {
                ShowError($"UnrealPak failed (exit {proc.ExitCode}):\n\n{stderr}\n{stdout}");
                return;
            }

            if (File.Exists(_pakPath))
            {
                var pakSize = new FileInfo(_pakPath).Length;
                ErrorLog.Write("EXPORT", new Exception($"Created pak size: {pakSize} bytes"));
            }
            else
            {
                ErrorLog.Write("EXPORT", new Exception("ERROR: Pak file was not created!"));
            }

            SetProgress(90);

            try
            {
                var stagingDir = Path.Combine(outputPath, "Sifu");
                if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, true);
            }
            catch { }

            try { Directory.Delete(tempDir, true); } catch { }

            var sigSource = @"C:\Users\Charles\Downloads\Sifu Modding\Unreal Pak Extracter and Creator\4.26\UE4\UnrealPak\pakchunk0-WindowsNoEditor.sig";
            var sigDest = Path.Combine(outputPath, Path.ChangeExtension(pakFileName, ".sig"));
            if (File.Exists(sigSource))
                File.Copy(sigSource, sigDest, true);

            SetProgress(100);
            UpdateStep(3, "done");

            string gameInstallDir = null;
            var searchDir = _contentPath;
            while (searchDir != null)
            {
                if (File.Exists(Path.Combine(searchDir, "Content", "Paks", "pakchunk0-WindowsNoEditor.sig")))
                {
                    gameInstallDir = searchDir;
                    break;
                }
                searchDir = Path.GetDirectoryName(searchDir);
            }

            var installedTo = "";
            if (gameInstallDir != null)
            {
                var modsDir = Path.Combine(gameInstallDir, "Content", "Paks", "~mods");
                Directory.CreateDirectory(modsDir);
                var destPak = Path.Combine(modsDir, pakFileName);
                File.Copy(_pakPath, destPak, true);
                installedTo = destPak;

                var gameSig = Path.Combine(gameInstallDir, "Content", "Paks", "pakchunk0-WindowsNoEditor.sig");
                var destSig = Path.Combine(modsDir, Path.ChangeExtension(pakFileName, ".sig"));
                if (File.Exists(gameSig))
                    File.Copy(gameSig, destSig, true);
            }

            var totalChanges = _modifiedNodes.Count + (hasStanceChange ? 1 : 0);
            ShowComplete(pakFileName, totalChanges, installedTo);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("EXPORT", ex);
            ShowError($"Export failed: {ex.Message}");
        }
    }

    private static string NormalizeSlotKey(string path)
    {
        path = path.Replace("\\", "/").TrimStart('/');
        if (!path.StartsWith("/"))
            path = "/" + path;
        var lastSlash = path.LastIndexOf('/');
        var lastDot = path.LastIndexOf('.');
        if (lastDot < lastSlash)
            path = path + "." + path.Substring(lastSlash + 1);
        return path;
    }

    private static string EnsureLeadingSlash(string path)
    {
        path = path.Replace("\\", "/").TrimStart('/');
        if (!path.StartsWith("/"))
            path = "/" + path;
        var lastDot = path.LastIndexOf('.');
        var lastSlash = path.LastIndexOf('/');
        if (lastDot > lastSlash)
            path = path[..lastDot];
        return path;
    }

    private static string GetKeyString(PropertyData key)
    {
        if (key is StrPropertyData sp) return sp.Value?.ToString() ?? "";
        if (key is NamePropertyData np) return np.Value?.Value?.ToString() ?? "";
        return key.ToString() ?? "";
    }

    private static int FindImport(UAsset asset, string attackName, string attackPath)
    {
        for (int i = 0; i < asset.Imports.Count; i++)
        {
            var imp = asset.Imports[i];
            string objName = imp.ObjectName?.Value?.ToString() ?? "";
            if (objName == attackName)
            {
                int outerRaw = imp.OuterIndex.Index;
                if (outerRaw < 0)
                {
                    int outerImpIdx = -(outerRaw + 1);
                    if (outerImpIdx < asset.Imports.Count)
                    {
                        string pkgName = asset.Imports[outerImpIdx].ObjectName?.Value?.ToString() ?? "";
                        if (pkgName == attackPath) return i;
                    }
                }
            }
        }
        return -1;
    }

    private static int AddAttackDBImport(UAsset asset, string attackName, string attackPath)
    {
        if (string.IsNullOrEmpty(attackName))
            attackName = Path.GetFileNameWithoutExtension(attackPath);
        if (string.IsNullOrEmpty(attackName))
            attackName = "UnknownAttack";

        var pkgImport = new UAssetAPI.Import();
        pkgImport.ClassPackage = FName.FromString(asset, "/Script/CoreUObject");
        pkgImport.ClassName = FName.FromString(asset, "Package");
        pkgImport.ObjectName = FName.FromString(asset, attackPath);
        pkgImport.OuterIndex = new FPackageIndex(0);
        pkgImport.PackageName = FName.FromString(asset, "None");
        int pkgIdx = asset.Imports.Count;
        asset.Imports.Add(pkgImport);

        var atkImport = new UAssetAPI.Import();
        atkImport.ClassPackage = FName.FromString(asset, "/Script/Sifu");
        atkImport.ClassName = FName.FromString(asset, "AttackDB");
        atkImport.ObjectName = FName.FromString(asset, attackName);
        atkImport.OuterIndex = FPackageIndex.FromImport(pkgIdx);
        atkImport.PackageName = FName.FromString(asset, "None");
        int atkIdx = asset.Imports.Count;
        asset.Imports.Add(atkImport);

        return atkIdx;
    }

    private static List<MapPropertyData> FindAllMapsNamed(List<PropertyData> props, string mapName)
    {
        var results = new List<MapPropertyData>();
        foreach (var p in props)
        {
            if (p is MapPropertyData mp && p.Name.Value?.ToString() == mapName)
                results.Add(mp);
            RecurseFindMaps(p, mapName, results);
        }
        return results;
    }

    private static void RecurseFindMaps(PropertyData p, string mapName, List<MapPropertyData> results)
    {
        if (p is StructPropertyData sp && sp.Value != null)
        {
            foreach (var child in sp.Value)
            {
                if (child is MapPropertyData mp2 && child.Name.Value?.ToString() == mapName)
                    results.Add(mp2);
                RecurseFindMaps(child, mapName, results);
            }
        }
        else if (p is ArrayPropertyData ap && ap.Value != null)
        {
            foreach (var child in ap.Value)
            {
                if (child is MapPropertyData mp3 && child.Name.Value?.ToString() == mapName)
                    results.Add(mp3);
                RecurseFindMaps(child, mapName, results);
            }
        }
    }

    private static void PatchStanceAsset(string vanillaPath, string charSpecificPath, string outputPath, EngineVersion eng, string? referenceDir, string stanceName, string fileName)
    {
        var vanillaSize = new FileInfo(vanillaPath).Length;
        ErrorLog.Write("EXPORT", new Exception($"[STANCE] Vanilla: {vanillaSize}B"));

        if (fileName == "BP_TransitionAnimRequest" && StanceGenerator.HasStance(stanceName))
        {
            try
            {
                string templatePath = Path.Combine(referenceDir ?? "", "StanceTemplateBase", fileName + ".uasset");
                if (!File.Exists(templatePath))
                    templatePath = vanillaPath;
                StanceGenerator.GenerateTransitionAnimRequest(templatePath, outputPath, stanceName, eng);
                var genUexp = Path.ChangeExtension(outputPath, ".uexp");
                if (!File.Exists(genUexp))
                {
                    var srcUexp = Path.Combine(referenceDir ?? "", "StanceTemplateBase", fileName + ".uexp");
                    if (File.Exists(srcUexp))
                        File.Copy(srcUexp, genUexp, overwrite: true);
                }
                return;
            }
            catch (Exception ex)
            {
                ErrorLog.Write("EXPORT", new Exception($"[STANCE] StanceGenerator failed for '{stanceName}' transition: {ex.Message}"));
            }
        }

        if (fileName == "BaseMovementDB" && StanceGenerator.HasStance(stanceName))
        {
            try
            {
                string templatePath = Path.Combine(referenceDir ?? "", "StanceTemplateBase", fileName + ".uasset");
                if (!File.Exists(templatePath))
                    templatePath = vanillaPath;
                StanceGenerator.GenerateBaseMovementDB(templatePath, outputPath, stanceName, eng);
                var genUexp = Path.ChangeExtension(outputPath, ".uexp");
                if (!File.Exists(genUexp))
                {
                    var srcUexp = Path.Combine(referenceDir ?? "", "StanceTemplateBase", fileName + ".uexp");
                    if (File.Exists(srcUexp))
                        File.Copy(srcUexp, genUexp, overwrite: true);
                }
                return;
            }
            catch (Exception ex)
            {
                ErrorLog.Write("EXPORT", new Exception($"[STANCE] StanceGenerator failed for '{stanceName}' BaseMovementDB: {ex.Message}"));
            }
        }

        if (!string.IsNullOrEmpty(referenceDir))
        {
            var refDir = Path.Combine(referenceDir, stanceName);
            var refFile = Path.Combine(refDir, fileName + ".uasset");
            if (File.Exists(refFile))
            {
                File.Copy(refFile, outputPath, overwrite: true);
                var refUexp = Path.Combine(refDir, fileName + ".uexp");
                if (File.Exists(refUexp))
                    File.Copy(refUexp, Path.ChangeExtension(outputPath, ".uexp"), overwrite: true);
                ErrorLog.Write("EXPORT", new Exception($"[STANCE] Used pre-made reference: {refFile} ({new FileInfo(refFile).Length}B)"));
                return;
            }
        }

        ErrorLog.Write("EXPORT", new Exception($"[STANCE] No reference for '{stanceName}', copying vanilla as-is"));
        File.Copy(vanillaPath, outputPath, overwrite: true);
        var vanillaUexp = Path.ChangeExtension(vanillaPath, ".uexp");
        if (File.Exists(vanillaUexp))
            File.Copy(vanillaUexp, Path.ChangeExtension(outputPath, ".uexp"), overwrite: true);
    }

    private static bool PatchDbAnimation(string vanillaDbPath, string newAnimPath, string outputDbPath, EngineVersion eng)
    {
        try
        {
            var asset = new UAsset(vanillaDbPath, eng, null, CustomSerializationFlags.None);

            NormalExport? dbExport = null;
            foreach (var exp in asset.Exports)
            {
                if (exp is NormalExport ne)
                {
                    dbExport = ne;
                    break;
                }
            }
            if (dbExport == null) return false;

            var mAttack = dbExport.Data.FirstOrDefault(p => p.Name?.Value?.ToString() == "m_Attack");
            if (mAttack is not StructPropertyData attackStruct || attackStruct.Value == null) return false;

            var mAnim = attackStruct.Value.FirstOrDefault(p => p.Name?.Value?.ToString() == "m_Animation");
            if (mAnim is not ObjectPropertyData animProp || animProp.Value == null) return false;

            if (animProp.Value.Index >= 0) return false;

            int importIndex = -(animProp.Value.Index + 1);
            if (importIndex < 0 || importIndex >= asset.Imports.Count) return false;

            string animName = newAnimPath.Contains('/') ? newAnimPath.Substring(newAnimPath.LastIndexOf('/') + 1) : newAnimPath;
            string pkgPath = "/" + newAnimPath;

            var currentImport = asset.Imports[importIndex];
            currentImport.ObjectName = FName.FromString(asset, animName);

            int outerIdx = -(currentImport.OuterIndex.Index + 1);
            if (outerIdx >= 0 && outerIdx < asset.Imports.Count)
            {
                asset.Imports[outerIdx].ObjectName = FName.FromString(asset, pkgPath);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(outputDbPath)!);
            asset.Write(outputDbPath);
            ErrorLog.Write("EXPORT", new Exception($"[DB PATCH] Modified {Path.GetFileName(vanillaDbPath)}: anim -> {animName} ({new FileInfo(outputDbPath).Length}B)"));
            return true;
        }
        catch (Exception ex)
        {
            ErrorLog.Write("EXPORT", new Exception($"[DB PATCH] Failed to patch {Path.GetFileName(vanillaDbPath)}: {ex.Message}"));
            return false;
        }
    }

    private static bool PatchDataTableAnim(string vanillaDtPath, string attackName, string newAnimPath, string outputDtPath, EngineVersion eng, Dictionary<string, (float hitFrame, int buildupFrame)>? animToTiming = null)
    {
        try
        {
            var asset = new UAsset(vanillaDtPath, eng, null, CustomSerializationFlags.None);

            DataTableExport? dtExport = null;
            foreach (var exp in asset.Exports)
            {
                if (exp is DataTableExport dte) { dtExport = dte; break; }
            }
            if (dtExport == null) return false;

            string animPath = newAnimPath.Contains('/') ? newAnimPath : "/" + newAnimPath;

            // Try Item indexer first (by FName key)
            bool patched = false;
            try
            {
                var row = dtExport[FName.FromString(asset, attackName)];
                if (row != null)
                {
                    patched = PatchRowAnim(row, animPath, asset);
                    if (patched && animToTiming != null) PatchRowTiming(row, animPath, animToTiming, asset);
                }
            }
            catch { }

            // Fallback: scan Data list for RowMap array
            if (!patched)
            {
                try
                {
                    var dataProps = dtExport.Data;
                    if (dataProps != null)
                    {
                        foreach (var prop in dataProps)
                        {
                            if (prop is ArrayPropertyData arr && arr.Name?.Value?.ToString() == "RowMap")
                            {
                                foreach (var elem in arr.Value)
                                {
                                    if (elem is StructPropertyData rowStruct)
                                    {
                                        var rowName = rowStruct.Name?.Value?.ToString();
                                        if (rowName == attackName)
                                        {
                                            patched = PatchRowAnim(rowStruct, animPath, asset);
                                            if (patched && animToTiming != null) PatchRowTiming(rowStruct, animPath, animToTiming, asset);
                                            if (patched) break;
                                        }
                                    }
                                }
                                if (patched) break;
                            }
                        }
                    }
                }
                catch { }
            }

            if (!patched) return false;

            Directory.CreateDirectory(Path.GetDirectoryName(outputDtPath)!);
            asset.Write(outputDtPath);
            ErrorLog.Write("EXPORT", new Exception($"[DT PATCH] Modified {Path.GetFileName(vanillaDtPath)}: {attackName} -> {Path.GetFileName(animPath)} ({new FileInfo(outputDtPath).Length}B)"));
            return true;
        }
        catch (Exception ex)
        {
            ErrorLog.Write("EXPORT", new Exception($"[DT PATCH] Failed to patch {Path.GetFileName(vanillaDtPath)}: {ex.Message}"));
            return false;
        }
    }

    private static bool PatchRowAnim(PropertyData rowProp, string animPath, UAsset asset)
    {
        if (rowProp is not StructPropertyData rowStruct || rowStruct.Value == null) return false;

        foreach (var prop in rowStruct.Value)
        {
            if (prop.Name?.Value?.ToString() == "m_Anim" && prop is SoftObjectPropertyData sop)
            {
                // For UE4.26 (pre-5.1), FTopLevelAssetPath.AssetName stores the full path
                var newPath = new FSoftObjectPath
                {
                    AssetPath = new FTopLevelAssetPath { PackageName = default, AssetName = FName.FromString(asset, animPath) },
                    SubPathString = sop.Value.SubPathString
                };
                sop.Value = newPath;
                return true;
            }
        }
        return false;
    }

    private static bool PatchRowTiming(PropertyData rowProp, string sourceAnimPath,
        Dictionary<string, (float hitFrame, int buildupFrame)> animToTiming, UAsset asset)
    {
        if (rowProp is not StructPropertyData rowStruct || rowStruct.Value == null) return false;
        if (!animToTiming.TryGetValue(sourceAnimPath, out var timing)) return false;

        bool patched = false;
        foreach (var prop in rowStruct.Value)
        {
            if (prop.Name?.Value?.ToString() == "m_fHitFrame" && prop is FloatPropertyData fp)
            {
                fp.Value = timing.hitFrame;
                patched = true;
            }
            else if (prop.Name?.Value?.ToString() == "m_iLastBuildupFrame" && prop is IntPropertyData ip)
            {
                ip.Value = timing.buildupFrame;
                patched = true;
            }
        }
        return patched;
    }

    private static void BinaryPatchDataTableTiming(
        string vanillaDtPath, string outDtPath,
        string rowName, float newHitFrame, int newBuildupFrame,
        Dictionary<string, (float hitFrame, int buildupFrame)> animToTiming, string animPath)
    {
        try
        {
            if (!animToTiming.TryGetValue(animPath, out var timing)) return;
            newHitFrame = timing.hitFrame;
            newBuildupFrame = timing.buildupFrame;

            var vanillaUexp = Path.ChangeExtension(vanillaDtPath, ".uexp");
            var outUexp = Path.ChangeExtension(outDtPath, ".uexp");
            if (!File.Exists(vanillaUexp)) return;

            var nameTable = ReadNameTable(vanillaDtPath);
            if (nameTable.Count == 0)
            {
                ErrorLog.Write("EXPORT", new Exception($"  BIN-DT SKIP: empty name table for {Path.GetFileName(vanillaDtPath)}"));
                return;
            }

            int rowNameIdx = -1, hitFrameIdx = -1, buildupIdx = -1;
            int floatTypeIdx = -1, intTypeIdx = -1;
            for (int i = 0; i < nameTable.Count; i++)
            {
                if (nameTable[i] == rowName) rowNameIdx = i;
                if (nameTable[i] == "m_fHitFrame") hitFrameIdx = i;
                if (nameTable[i] == "m_iLastBuildupFrame") buildupIdx = i;
                if (nameTable[i] == "FloatProperty") floatTypeIdx = i;
                if (nameTable[i] == "IntProperty") intTypeIdx = i;
            }

            if (rowNameIdx < 0 || hitFrameIdx < 0 || buildupIdx < 0 || floatTypeIdx < 0 || intTypeIdx < 0)
            {
                ErrorLog.Write("EXPORT", new Exception($"  BIN-DT SKIP: name indices not found (row={rowNameIdx} hit={hitFrameIdx} build={buildupIdx})"));
                return;
            }

            var bytes = File.ReadAllBytes(vanillaUexp);
            int patched = 0;

            byte[] rowKey = BitConverter.GetBytes(rowNameIdx);

            int rowStart = FindBytes(bytes, rowKey, 0);
            if (rowStart < 0)
            {
                ErrorLog.Write("EXPORT", new Exception($"  BIN-DT SKIP: row '{rowName}' FName[{rowNameIdx}] not found in .uexp"));
                return;
            }

            int searchEnd = Math.Min(bytes.Length, rowStart + 2000);

            int hitFrameTag = FindPropertyTag(bytes, hitFrameIdx, floatTypeIdx, rowStart, searchEnd);
            if (hitFrameTag >= 0)
            {
                int valueOffset = hitFrameTag + 25;
                if (valueOffset + 4 <= bytes.Length)
                {
                    BitConverter.GetBytes(newHitFrame).CopyTo(bytes, valueOffset);
                    patched++;
                    ErrorLog.Write("EXPORT", new Exception($"  BIN-DT: {rowName} m_fHitFrame = {newHitFrame} (offset {valueOffset})"));
                }
            }

            int buildupTag = FindPropertyTag(bytes, buildupIdx, intTypeIdx, rowStart, searchEnd);
            if (buildupTag >= 0)
            {
                int valueOffset = buildupTag + 25;
                if (valueOffset + 4 <= bytes.Length)
                {
                    BitConverter.GetBytes(newBuildupFrame).CopyTo(bytes, valueOffset);
                    patched++;
                    ErrorLog.Write("EXPORT", new Exception($"  BIN-DT: {rowName} m_iLastBuildupFrame = {newBuildupFrame} (offset {valueOffset})"));
                }
            }

            if (patched > 0)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(outUexp)!);
                File.WriteAllBytes(outUexp, bytes);
                ErrorLog.Write("EXPORT", new Exception($"  BIN-DT PATCHED {patched}/2 timing values for '{rowName}' in {Path.GetFileName(outUexp)}"));
            }
            else
            {
                ErrorLog.Write("EXPORT", new Exception($"  BIN-DT: no timing tags found for '{rowName}' (rowStart={rowNameIdx}, search {rowStart}-{searchEnd})"));
            }
        }
        catch (Exception ex)
        {
            ErrorLog.Write("EXPORT", new Exception($"  BIN-DT FAIL: {rowName}: {ex.Message}"));
        }
    }

    private static bool BinaryPatchComboImports(
        UAsset vanillaAsset, string vanillaUassetPath, string outUassetPath,
        List<(string oldShortName, string newShortName, string newFullPath)> swaps)
    {
        try
        {
            var bytes = File.ReadAllBytes(vanillaUassetPath);

            int GetField(string name)
            {
                var f = typeof(UAsset).GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                return f != null ? (int)f.GetValue(vanillaAsset)! : 0;
            }

            int nameOffset  = GetField("NameOffset");
            int nameCount   = GetField("NameCount");
            int importOffset = GetField("ImportOffset");
            int importCount  = GetField("ImportCount");
            int exportOffset = GetField("ExportOffset");
            int dependsOffset = GetField("DependsOffset");
            int preloadDepOffset = GetField("PreloadDependencyOffset");
            int sectionSixOffset = GetField("SectionSixOffset");
            int assetRegOffset = GetField("AssetRegistryDataOffset");

            ErrorLog.Write("EXPORT", new Exception($"  BIN-IMP: header offsets: NameOff={nameOffset} NameCnt={nameCount} ImpOff={importOffset} ImpCnt={importCount} ExpOff={exportOffset} DepOff={dependsOffset} PreDepOff={preloadDepOffset} Sec6Off={sectionSixOffset}"));

            var nameMap = vanillaAsset.GetNameMapIndexList();

            var names = new List<string>();
            var nameToIdx = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < nameMap.Count; i++)
            {
                string name = nameMap[i].ToString() ?? "";
                names.Add(name);
                nameToIdx[name] = i;
            }

            var nameEntrySizes = new List<int>();
            int pos = nameOffset;
            for (int i = 0; i < nameCount && pos + 4 < bytes.Length; i++)
            {
                int entryStart = pos;
                int strLen = BitConverter.ToInt32(bytes, pos);
                pos += 4;
                if (strLen < 0) pos += -strLen * 2;
                else pos += strLen;
                pos += 4;
                nameEntrySizes.Add(pos - entryStart);
            }
            int oldNameTableEnd = pos;

            const int IMPORT_ENTRY_SIZE = 28;

            var swapsToMake = new List<(int importIndex, int outerImportIndex, string newShortName, string newFullPath)>();
            for (int i = 0; i < importCount; i++)
            {
                int impOff = importOffset + i * IMPORT_ENTRY_SIZE;
                int objNameIdxOff = impOff + 20;
                if (objNameIdxOff + 4 > bytes.Length) continue;
                int objNameIdx = BitConverter.ToInt32(bytes, objNameIdxOff);
                if (objNameIdx < 0 || objNameIdx >= names.Count) continue;
                string objName = names[objNameIdx];
                foreach (var (oldShort, newShort, newFull) in swaps)
                {
                    if (objName == oldShort)
                    {
                        int rawOuterIdx = BitConverter.ToInt32(bytes, impOff + 16);
                        int outerIdx = rawOuterIdx < 0 ? (-rawOuterIdx - 1) : rawOuterIdx;
                        swapsToMake.Add((i, outerIdx, newShort, newFull));
                        break;
                    }
                }
            }

            if (swapsToMake.Count == 0)
            {
                ErrorLog.Write("EXPORT", new Exception("  BIN-IMP: no matching imports to swap"));
                return false;
            }

            var namesToAdd = new List<string>();
            foreach (var (_, _, newShort, newFull) in swapsToMake)
            {
                if (!nameToIdx.ContainsKey(newShort)) namesToAdd.Add(newShort);
                if (!nameToIdx.ContainsKey(newFull)) namesToAdd.Add(newFull);
            }

            using var newNameTableMs = new MemoryStream();
            pos = nameOffset;
            for (int i = 0; i < nameCount; i++)
            {
                newNameTableMs.Write(bytes, pos, nameEntrySizes[i]);
                pos += nameEntrySizes[i];
            }
            foreach (var newName in namesToAdd)
            {
                nameToIdx[newName] = names.Count;
                names.Add(newName);
                int strLen = newName.Length + 1;
                byte[] strBytes = System.Text.Encoding.ASCII.GetBytes(newName + "\0");
                newNameTableMs.Write(BitConverter.GetBytes(strLen), 0, 4);
                newNameTableMs.Write(strBytes, 0, strBytes.Length);
                newNameTableMs.Write(new byte[4], 0, 4);
            }
            byte[] newNameTableBytes = newNameTableMs.ToArray();
            int nameTableDelta = newNameTableBytes.Length - (oldNameTableEnd - nameOffset);

            int oldImportTableEnd = importOffset + importCount * IMPORT_ENTRY_SIZE;

            var swapsDict = new Dictionary<int, string>();
            var outerSwapsDict = new Dictionary<int, string>();
            foreach (var (impIdx, outerIdx, newShort, newFull) in swapsToMake)
            {
                swapsDict[impIdx] = newShort;
                if (outerIdx >= 0 && outerIdx < importCount)
                    outerSwapsDict[outerIdx] = newFull;
            }

            using var newImportMs = new MemoryStream();
            for (int i = 0; i < importCount; i++)
            {
                int impOff = importOffset + i * IMPORT_ENTRY_SIZE;
                newImportMs.Write(bytes, impOff, 20);
                if (swapsDict.TryGetValue(i, out var newShort))
                {
                    newImportMs.Write(BitConverter.GetBytes(nameToIdx[newShort]), 0, 4);
                    newImportMs.Write(BitConverter.GetBytes(0), 0, 4);
                }
                else if (outerSwapsDict.TryGetValue(i, out var newFull))
                {
                    newImportMs.Write(BitConverter.GetBytes(nameToIdx[newFull]), 0, 4);
                    newImportMs.Write(BitConverter.GetBytes(0), 0, 4);
                }
                else
                {
                    newImportMs.Write(bytes, impOff + 20, 8);
                }
            }
            byte[] newImportTableBytes = newImportMs.ToArray();

            using var outMs = new MemoryStream(bytes.Length + nameTableDelta);
            outMs.Write(bytes, 0, nameOffset);
            outMs.Write(newNameTableBytes, 0, newNameTableBytes.Length);
            if (oldNameTableEnd < importOffset)
                outMs.Write(bytes, oldNameTableEnd, importOffset - oldNameTableEnd);
            outMs.Write(newImportTableBytes, 0, newImportTableBytes.Length);
            if (oldImportTableEnd < bytes.Length)
                outMs.Write(bytes, oldImportTableEnd, bytes.Length - oldImportTableEnd);

            var resultBytes = outMs.ToArray();

            int patchedNameCount = 0, patchedImportOffset = 0, patchedExportOffset = 0;
            int patchedDependsOffset = 0, patchedPreloadDepOffset = 0, patchedSectionSix = 0;
            int patchedAssetReg = 0;

            for (int i = 8; i < nameOffset - 3; i++)
            {
                int val = BitConverter.ToInt32(resultBytes, i);
                if (val == nameCount)
                {
                    BitConverter.GetBytes(nameCount + namesToAdd.Count).CopyTo(resultBytes, i);
                    patchedNameCount++;
                    ErrorLog.Write("EXPORT", new Exception($"  BIN-IMP: patched NameCount at 0x{i:X}"));
                }
                if (val == importOffset)
                {
                    BitConverter.GetBytes(importOffset + nameTableDelta).CopyTo(resultBytes, i);
                    patchedImportOffset++;
                    ErrorLog.Write("EXPORT", new Exception($"  BIN-IMP: patched ImportOffset at 0x{i:X}"));
                }
                if (val == exportOffset && exportOffset != importOffset)
                {
                    BitConverter.GetBytes(exportOffset + nameTableDelta).CopyTo(resultBytes, i);
                    patchedExportOffset++;
                    ErrorLog.Write("EXPORT", new Exception($"  BIN-IMP: patched ExportOffset at 0x{i:X}"));
                }
                if (val == dependsOffset && dependsOffset != 0 && dependsOffset != exportOffset)
                {
                    BitConverter.GetBytes(dependsOffset + nameTableDelta).CopyTo(resultBytes, i);
                    patchedDependsOffset++;
                    ErrorLog.Write("EXPORT", new Exception($"  BIN-IMP: patched DependsOffset at 0x{i:X}"));
                }
                if (val == preloadDepOffset && preloadDepOffset != 0 && preloadDepOffset != dependsOffset)
                {
                    BitConverter.GetBytes(preloadDepOffset + nameTableDelta).CopyTo(resultBytes, i);
                    patchedPreloadDepOffset++;
                    ErrorLog.Write("EXPORT", new Exception($"  BIN-IMP: patched PreloadDepOffset at 0x{i:X}"));
                }
                if (val == sectionSixOffset && sectionSixOffset != 0 && sectionSixOffset != preloadDepOffset)
                {
                    BitConverter.GetBytes(sectionSixOffset + nameTableDelta).CopyTo(resultBytes, i);
                    patchedSectionSix++;
                    ErrorLog.Write("EXPORT", new Exception($"  BIN-IMP: patched SectionSixOffset at 0x{i:X}"));
                }
                if (val == assetRegOffset && assetRegOffset != 0)
                {
                    BitConverter.GetBytes(assetRegOffset + nameTableDelta).CopyTo(resultBytes, i);
                    patchedAssetReg++;
                    ErrorLog.Write("EXPORT", new Exception($"  BIN-IMP: patched AssetRegistryDataOffset at 0x{i:X}"));
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(outUassetPath)!);
            File.WriteAllBytes(outUassetPath, resultBytes);
            ErrorLog.Write("EXPORT", new Exception($"  BIN-IMP: patched {swapsToMake.Count} imports, added {namesToAdd.Count} names, {resultBytes.Length}B, delta={nameTableDelta}"));
            return true;
        }
        catch (Exception ex)
        {
            ErrorLog.Write("EXPORT", new Exception($"  BIN-IMP FAIL: {ex.Message}\n{ex.StackTrace}"));
            return false;
        }
    }

    private static int FindBytes(byte[] haystack, byte[] needle, int startOffset)
    {
        for (int i = startOffset; i <= haystack.Length - needle.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j]) { match = false; break; }
            }
            if (match) return i;
        }
        return -1;
    }

    private static int FindPropertyTag(byte[] bytes, int nameIdx, int typeIdx, int startOffset, int endOffset)
    {
        byte[] nameBytes = BitConverter.GetBytes(nameIdx);
        byte[] typeBytes = BitConverter.GetBytes(typeIdx);

        for (int i = startOffset; i <= endOffset - 17; i++)
        {
            if (bytes[i] == nameBytes[0] && bytes[i + 1] == nameBytes[1] &&
                bytes[i + 2] == nameBytes[2] && bytes[i + 3] == nameBytes[3] &&
                bytes[i + 8] == typeBytes[0] && bytes[i + 9] == typeBytes[1] &&
                bytes[i + 10] == typeBytes[2] && bytes[i + 11] == typeBytes[3])
            {
                return i;
            }
        }
        return -1;
    }

    private static List<string> ReadNameTable(string uassetPath)
    {
        var result = new List<string>();
        try
        {
            var bytes = File.ReadAllBytes(uassetPath);
            if (bytes.Length < 0x1C) return result;

            int legacyVersion = BitConverter.ToInt32(bytes, 0x04);

            int offset = 0x1C;
            if (legacyVersion < -2)
            {
                offset = BitConverter.ToInt32(bytes, 0x1C);
                if (offset < 0x1C || offset > bytes.Length) offset = 0x1C;
            }

            if (offset + 8 > bytes.Length) return result;
            int nameCount = BitConverter.ToInt32(bytes, offset);
            offset += 4;
            int nameOffset = BitConverter.ToInt32(bytes, offset);

            if (nameOffset < 0 || nameOffset >= bytes.Length) return result;
            int pos = nameOffset;

            for (int i = 0; i < nameCount && pos + 4 < bytes.Length; i++)
            {
                int strLen = BitConverter.ToInt32(bytes, pos);
                pos += 4;

                if (strLen < 0)
                {
                    strLen = -strLen * 2;
                    if (pos + strLen > bytes.Length) break;
                    var s = System.Text.Encoding.Unicode.GetString(bytes, pos, strLen - 2);
                    result.Add(s);
                    pos += strLen;
                }
                else
                {
                    if (pos + strLen > bytes.Length) break;
                    var s = System.Text.Encoding.ASCII.GetString(bytes, pos, strLen - 1);
                    result.Add(s);
                    pos += strLen;
                }

                pos += 4;
            }
        }
        catch { }
        return result;
    }

    private static void CopyDirectory(string srcDir, string destDir)
    {
        foreach (var srcFile in Directory.GetFiles(srcDir, "*.*", SearchOption.AllDirectories))
        {
            string ext = Path.GetExtension(srcFile);
            if (ext == ".bak") continue;
            if (ext != ".uasset" && ext != ".uexp") continue;

            string relPath = Path.GetRelativePath(srcDir, srcFile);
            string dest = Path.Combine(destDir, relPath);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(srcFile, dest, overwrite: true);
        }
    }

    private static void CopyDirectorySkipPatched(string srcDir, string destDir)
    {
        foreach (var srcFile in Directory.GetFiles(srcDir, "*.*", SearchOption.AllDirectories))
        {
            string ext = Path.GetExtension(srcFile);
            if (ext == ".bak") continue;
            if (ext != ".uasset" && ext != ".uexp") continue;

            string relPath = Path.GetRelativePath(srcDir, srcFile);
            string dest = Path.Combine(destDir, relPath);
            if (File.Exists(dest)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(srcFile, dest, overwrite: false);
        }
    }

    private void SetProgress(double percent)
    {
        var parentWidth = ((FrameworkElement)progressFill.Parent).RenderSize.Width;
        var targetWidth = (percent / 100.0) * parentWidth;
        var anim = new DoubleAnimation(targetWidth, TimeSpan.FromMilliseconds(300));
        progressFill.BeginAnimation(WidthProperty, anim);
        txtProgress.Text = $"{(int)percent}%";
    }

    private void UpdateStep(int step, string state)
    {
        var stepControl = step switch
        {
            1 => step1,
            2 => step2,
            3 => step3,
            _ => step1
        };

        var (symbol, color) = state switch
        {
            "active" => ("⏳", "#89b4fa"),
            "done" => ("✓", "#a6e3a1"),
            _ => ("○", "#6c7086")
        };

        stepControl.Text = $"{symbol} {stepControl.Text[2..]}";
        stepControl.Foreground = MakeBrush(color);
    }

    private void ShowError(string message)
    {
        panelExporting.Visibility = Visibility.Collapsed;
        panelReview.Visibility = Visibility.Visible;
        txtStatus.Text = message;
        txtStatus.Foreground = MakeBrush("#f38ba8");
        btnConfirm.IsEnabled = true;
    }

    private void ShowComplete(string pakName, int changeCount, string installedTo)
    {
        panelExporting.Visibility = Visibility.Collapsed;
        panelComplete.Visibility = Visibility.Visible;

        var pakSize = File.Exists(_pakPath) ? new FileInfo(_pakPath).Length : 0;
        var sizeStr = pakSize > 1024 * 1024
            ? $"{pakSize / (1024.0 * 1024.0):F1} MB"
            : $"{pakSize / 1024.0:F1} KB";

        txtResult.Text = $"{pakName} ({changeCount} changes, {sizeStr})";

        if (!string.IsNullOrEmpty(installedTo))
        {
            txtInstallPath.Text = $"Installed to:\n{installedTo}";
        }
        else
        {
            txtInstallPath.Text = $"Saved to: {_outputDir}\nCopy to your Sifu/Content/Paks/~mods/ folder.";
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_outputDir) && Directory.Exists(_outputDir))
        {
            Process.Start("explorer.exe", _outputDir);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
