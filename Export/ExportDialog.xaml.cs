using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
        string? referenceModDir = null)
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
        txtOutput.Text = $"Output: {outputDir}/MainCharComboMod.pak + .sig";
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
                        if (string.IsNullOrEmpty(node.DefaultDBPath))
                        {
                            skippedEmpty++;
                            if (skippedEmpty <= 3)
                                ErrorLog.Write("EXPORT", new Exception($"  SKIP(empty DB): {node.DisplayName} AnimPath='{node.AnimPath}' DefaultDBPath='{node.DefaultDBPath}' DefaultAnimPath='{node.DefaultAnimPath}'"));
                            continue;
                        }
                        if (!_animToDbPath.TryGetValue(node.AnimPath, out var newDbPath))
                        {
                            var vanillaDbFile = Path.Combine(gameRoot, node.DefaultDBPath.TrimStart('/') + ".uasset");
                            if (!patchedDbFiles.Contains(node.DefaultDBPath) && File.Exists(vanillaDbFile))
                            {
                                var relDbPath = node.DefaultDBPath.TrimStart('/') + ".uasset";
                                var outDbPath = Path.Combine(outputPath, "Sifu", "Content", relDbPath);

                                if (PatchDbAnimation(vanillaDbFile, node.AnimPath, outDbPath, EngineVersion.VER_UE4_26))
                                {
                                    patchedDbFiles.Add(node.DefaultDBPath);
                                    var outDbUexp = Path.ChangeExtension(outDbPath, ".uexp");
                                    var vanillaDbUexp = Path.ChangeExtension(vanillaDbFile, ".uexp");
                                    if (File.Exists(vanillaDbUexp) && !File.Exists(outDbUexp))
                                        File.Copy(vanillaDbUexp, outDbUexp, true);

                                    fileEntries.Add((outDbPath, "../../../Sifu/Content/" + relDbPath));
                                    fileEntries.Add((outDbUexp, "../../../Sifu/Content/" + relDbPath.Replace(".uasset", ".uexp")));

                                    patchedFallback++;
                                    patched++;
                                    ErrorLog.Write("EXPORT", new Exception($"  PATCHED (fallback): {node.DisplayName} -> {node.AnimPath} (modified {Path.GetFileName(node.DefaultDBPath)})"));
                                    continue;
                                }
                            }

                            skippedNoDb++;
                            if (skippedNoDb <= 3)
                                ErrorLog.Write("EXPORT", new Exception($"  SKIP(no anim->db): {node.DisplayName} AnimPath='{node.AnimPath}' DefaultDBPath='{node.DefaultDBPath}'"));
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

                    ErrorLog.Write("EXPORT", new Exception($"Patched {patched}/{_modifiedNodes.Count} nodes (direct: {patched - patchedFallback}, fallback DB: {patchedFallback}, skipped empty DB: {skippedEmpty}, skipped no anim->db: {skippedNoDb})"));

                    asset.Write(outUasset);
                });

                UpdateStep(2, "done");
                SetProgress(50);

                fileEntries.Add((outUasset, "../../../Sifu/Content/DB/_MainChar/Combos/MainChar_ComboTree.uasset"));
                fileEntries.Add((Path.ChangeExtension(outUasset, ".uexp"),
                    "../../../Sifu/Content/DB/_MainChar/Combos/MainChar_ComboTree.uexp"));

                var extractedPaksDir = Path.Combine(gameRoot, "..", "..", "Unreal Pak Extracter and Creator",
                    "extractedPaks", "pakchunk0-WindowsNoEditor", "Sifu", "Content");
                if (Directory.Exists(extractedPaksDir))
                {
                    var modifiedEnemyTypes = _modifiedNodes
                        .Where(n => !string.IsNullOrEmpty(n.AnimPath))
                        .Select(n =>
                        {
                            var path = n.AnimPath ?? "";
                            if (path.Contains("/AI/Archetypes/"))
                            {
                                var idx = path.IndexOf("/AI/Archetypes/") + "/AI/Archetypes/".Length;
                                var rest = path.Substring(idx);
                                var slash = rest.IndexOf('/');
                                return slash >= 0 ? rest.Substring(0, slash) : "";
                            }
                            return "";
                        })
                        .Where(t => !string.IsNullOrEmpty(t))
                        .Distinct()
                        .ToList();

                    foreach (var enemyType in modifiedEnemyTypes)
                    {
                        var srcDir = Path.Combine(extractedPaksDir, "DB", "AI", "Archetypes", enemyType, "Attacks");
                        if (!Directory.Exists(srcDir)) continue;

                        var destDir = Path.Combine(outputPath, "Sifu", "Content", "DB", "AI", "Archetypes", enemyType, "Attacks");
                        CopyDirectory(srcDir, destDir);

                        foreach (var f in Directory.GetFiles(destDir, "*.*", SearchOption.AllDirectories)
                            .Where(f => Path.GetExtension(f) == ".uasset" || Path.GetExtension(f) == ".uexp"))
                        {
                            var relInPak = Path.GetRelativePath(outputPath, f).Replace('\\', '/');
                            fileEntries.Add((f, "../../../Sifu/Content/" + relInPak));
                        }
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
            var pakFileName = "MainCharComboMod.pak";
            _pakPath = Path.Combine(outputPath, pakFileName);

            if (!File.Exists(pakExe))
            {
                ShowError($"UnrealPak not found at:\n{pakExe}");
                return;
            }

            SetProgress(75);

            var psi = new ProcessStartInfo
            {
                FileName = pakExe,
                Arguments = $"\"{_pakPath}\" -create=\"{filelistPath}\"",
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
