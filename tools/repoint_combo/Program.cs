using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;

internal static class Program
{
    const string VanillaComboRel = "DB/AI/Archetypes/Grunt/_Miniboss/Grunt_Miniboss_Combo";
    const string ClonedComboRel = "DB/AI/Archetypes/Grunt/_Base/Custom/Grunt_TestCombo";
    const string FDComboRel = "DB/AI/Archetypes/FireDisciple/Variations/Combo/FireDisciple_Base_Combo";
    const string YangMeshGamePath = "/Game/Characters/Boss/Yang/Meshes/SK_Yang_Hideout05";
    const string YangMeshObjectName = "SK_Yang_Hideout05";
    const string OriginalPakPath = @"C:\Program Files\Epic Games\Sifu\Sifu\Content\Paks\pakchunk0-WindowsNoEditor.pak";

    static readonly string[] PatchRels =
    {
        "DB/AI/Archetypes/Grunt/_Base/BP_Grunt_ArchetypeDB_Base",
        "DB/AI/Archetypes/Grunt/_MainGame/Generic/BP_Grunt_ArchetypeDB_Adv_Master",
        "DB/AI/Archetypes/Grunt/_Miniboss/BP_Grunt_Miniboss_DB",
        "DB/AI/Archetypes/Grunt/_Base/Hideout0/Special/BP_Grunt_ArchetypeDB_Qiaoge_Phase1",
        "DB/AI/Archetypes/Servant/BP_Servant_ArchetypeDB",
        "DB/AI/Archetypes/Servant/Variants/Hideout0/BP_H0_TrainingEntry_ArchetypeDB",
    };

    static readonly string[] CanaryDirs =
    {
        "DB/AI/Archetypes/Grunt",
        "DB/AI/Archetypes/Servant",
    };

    static readonly string[] MeshScenarioDbDirs =
    {
        "DB/AI/Archetypes/Grunt",
        "DB/AI/Archetypes/Servant",
    };

    static readonly string[] BlueprintScenarioFilters =
    {
        "*Content/Blueprints/TrainingRoom*",
        "*Content/Blueprints/Tutorial*",
    };

    static readonly (string Name, float Value)[] CanaryFields =
    {
        ("m_fHealth", 5f),
    };

    static readonly Regex WeaponTargetRe = new Regex("Blade|Bat|Staff|Weapon", RegexOptions.Compiled);
    static readonly Regex GruntArchDbRe = new Regex("ArchetypeDB|Miniboss_DB|LastMan_Db", RegexOptions.Compiled);
    static readonly EngineVersion Eng = EngineVersion.VER_UE4_26;

    sealed class ComboField
    {
        public string Path = "";
        public string PropName = "";
        public string Target = "";
        public bool InOverrides;
        public bool WeaponTarget;
        public bool IsComboProp => PropName == "m_Combo" || PropName == "m_ComboOverride";
        public bool IsBarehandPatch => IsComboProp && !WeaponTarget;
    }

    static int Main(string[] args)
    {
        try
        {
            if (args.Length == 2 && args[0] == "inspect")
                return Inspect(args[1]);

            if (args.Length == 2 && args[0] == "scan")
                return Scan(args[1]);

            if (args.Length == 3 && (args[0] == "a" || args[0] == "b" || args[0] == "c"))
                return BuildPak(args[0], args[1], args[2]);

            Console.WriteLine("Usage:");
            Console.WriteLine("  repoint_combo inspect <path-to-uasset>     dump combo/mesh/canary fields of one file");
            Console.WriteLine("  repoint_combo scan <dir>                   same for every .uasset under dir");
            Console.WriteLine("  repoint_combo a <contentRoot> <outDir>     Pak A3: Grunt + Servant barehand combo fields (incl. EE/fists slot) -> FireDisciple_Base_Combo + m_fHealth=5 load canary on all Grunt/Servant arch DBs");
            Console.WriteLine("  repoint_combo b <contentRoot> <outDir>     Pak B: clone miniboss combo to a new content path + repoint barehand fields at the clone (no canary)");
            Console.WriteLine("  repoint_combo c <contentRoot> <outDir>     Pak C: A3 combo repoint + canary, AND m_OverrideSkeletalMesh -> SK_Yang_Hideout05 on Grunt/Servant scenarios (DB + TrainingRoom/Tutorial)");
            return 2;
        }
        catch (Exception ex)
        {
            Console.WriteLine("ERROR: " + ex.Message);
            Console.WriteLine(ex.ToString());
            return 1;
        }
    }

    static int Inspect(string path)
    {
        if (!File.Exists(path)) { Console.WriteLine("missing: " + path); return 1; }
        var asset = new UAsset(path, Eng, null, CustomSerializationFlags.None);
        Console.WriteLine($"File : {path}");
        Console.WriteLine($"Exports: {asset.Exports.Count}, Imports: {asset.Imports.Count}");
        PrintFindings(asset);
        return 0;
    }

    static int Scan(string dir)
    {
        if (!Directory.Exists(dir)) { Console.WriteLine("missing dir: " + dir); return 1; }
        var files = Directory.GetFiles(dir, "*.uasset", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();
        int withFindings = 0;
        foreach (var f in files)
        {
            UAsset asset;
            try { asset = new UAsset(f, Eng, null, CustomSerializationFlags.None); }
            catch (Exception ex) { Console.WriteLine($"SKIP {f}: {ex.Message}"); continue; }
            var lines = CollectFindings(asset);
            if (lines.Count == 0) continue;
            withFindings++;
            Console.WriteLine(f);
            foreach (var l in lines) Console.WriteLine("  " + l);
        }
        Console.WriteLine($"scanned {files.Length} files, {withFindings} with combo/canary findings");
        return 0;
    }

    static void PrintFindings(UAsset asset)
    {
        var lines = CollectFindings(asset);
        if (lines.Count == 0) { Console.WriteLine("  (no combo/canary fields found)"); return; }
        foreach (var l in lines) Console.WriteLine("  " + l);
    }

    static List<string> CollectFindings(UAsset asset)
    {
        var lines = new List<string>();
        foreach (var exp in asset.Exports)
        {
            if (exp is not NormalExport ne || ne.Data == null) continue;
            WalkProps(ne.Data, "", (p, path) =>
            {
                if (p is ObjectPropertyData op)
                {
                    var name = op.Name.Value?.ToString() ?? "";
                    if (name is "m_Combo" or "m_ComboOverride")
                    {
                        var target = ResolveImport(asset, op.Value);
                        bool wpn = IsWeaponTarget(target);
                        string kind = wpn ? "weapon" : "barehand";
                        lines.Add($"{name,-16} [{kind}] @ {path} -> {target}");
                    }
                    else if (name == "m_OverrideSkeletalMesh")
                    {
                        var target = ResolveImport(asset, op.Value);
                        lines.Add($"{name,-16} [mesh]    @ {path} -> {target}");
                    }
                }
                else if (p is FloatPropertyData fp)
                {
                    var name = fp.Name.Value?.ToString() ?? "";
                    if (!CanaryFields.Any(c => c.Name == name)) return;
                    lines.Add($"{name,-16} [canary]  = {fp.Value}");
                }
            });
        }
        return lines;
    }

    static int BuildPak(string mode, string contentRoot, string outDir)
    {
        bool clone = mode == "b";
        bool withMesh = mode == "c";
        contentRoot = Path.GetFullPath(contentRoot);
        outDir = Path.GetFullPath(outDir);
        if (!Directory.Exists(contentRoot)) { Console.WriteLine("content root missing: " + contentRoot); return 1; }
        Directory.CreateDirectory(outDir);

        var stagingRoot = Path.Combine(outDir, "Sifu");
        if (Directory.Exists(stagingRoot)) Directory.Delete(stagingRoot, true);
        var stagingContent = Path.Combine(stagingRoot, "Content");
        Directory.CreateDirectory(stagingContent);

        var targetRel = clone ? ClonedComboRel : FDComboRel;
        var targetGamePath = "/Game/" + targetRel.Replace('\\', '/');
        bool includeCanary = !clone;

        var comboSet = new HashSet<string>(PatchRels, StringComparer.OrdinalIgnoreCase);
        var allRels = new List<string>(PatchRels);
        if (includeCanary)
        {
            foreach (var canaryDirRel in CanaryDirs)
            {
                var canaryDir = Path.Combine(contentRoot, canaryDirRel.Replace('/', Path.DirectorySeparatorChar));
                if (!Directory.Exists(canaryDir)) { Console.WriteLine("missing canary dir: " + canaryDir); return 1; }
                foreach (var f in Directory.GetFiles(canaryDir, "BP_*.uasset", SearchOption.AllDirectories))
                {
                    if (!GruntArchDbRe.IsMatch(Path.GetFileName(f))) continue;
                    var rel = Path.GetRelativePath(contentRoot, f).Replace('\\', '/');
                    if (rel.EndsWith(".uasset")) rel = rel.Substring(0, rel.Length - ".uasset".Length);
                    if (!allRels.Contains(rel, StringComparer.OrdinalIgnoreCase)) allRels.Add(rel);
                }
            }
            Console.WriteLine($"patch set: {comboSet.Count} combo file(s), {allRels.Count - PatchRels.Length} canary-only file(s)");
        }
        allRels.Sort(StringComparer.OrdinalIgnoreCase);

        var staged = new List<(string src, string dest)>();

        if (clone)
        {
            CopyAsset(contentRoot, stagingContent, VanillaComboRel);
            Console.WriteLine($"cloned  {VanillaComboRel} -> {ClonedComboRel}");
            AddStaged(staged, stagingContent, ClonedComboRel);
        }

        foreach (var archRel in allRels)
        {
            var srcUasset = Path.Combine(contentRoot, archRel.Replace('/', Path.DirectorySeparatorChar) + ".uasset");
            if (!File.Exists(srcUasset)) { Console.WriteLine("missing asset: " + srcUasset); return 1; }

            var stagedUasset = Path.Combine(stagingContent, archRel.Replace('/', Path.DirectorySeparatorChar) + ".uasset");
            Directory.CreateDirectory(Path.GetDirectoryName(stagedUasset)!);
            File.Copy(srcUasset, stagedUasset, true);
            var stagedUexp = Path.ChangeExtension(stagedUasset, ".uexp");
            var srcUexp = Path.ChangeExtension(srcUasset, ".uexp");
            if (File.Exists(srcUexp)) File.Copy(srcUexp, stagedUexp, true);

            bool comboFile = comboSet.Contains(archRel);
            Console.WriteLine($"=== {archRel} {(comboFile ? "(combo+canary)" : "(canary)")}");
            int rc = ProcessStaged(stagedUasset, targetGamePath, targetRel, comboFile, includeCanary);
            if (rc < 0) return 1;
            if (rc == 0)
            {
                try { File.Delete(stagedUasset); } catch { }
                try { if (File.Exists(stagedUexp)) File.Delete(stagedUexp); } catch { }
                continue;
            }

            AddStaged(staged, stagingContent, archRel);
        }

        if (withMesh)
        {
            if (StageMeshScenarios(contentRoot, stagingContent, staged) < 0) return 1;
        }

        if (staged.Count == 0) { Console.WriteLine("nothing staged"); return 1; }

        var pakName = clone ? "PoC_B_GruntCustomCombo.pak"
            : withMesh ? "PoC_C_GruntServantYang.pak"
            : "PoC_A3_GruntServantFDCombo.pak";
        var pakPath = Path.Combine(outDir, pakName);

        if (!TryFindUnrealPak(out var pakExe, out var pakWd))
        {
            Console.WriteLine("UnrealPak.exe not found (expected at <repo>\\tools\\ue4\\UnrealPak\\UnrealPak.exe)");
            return 1;
        }
        Console.WriteLine("UnrealPak: " + pakExe);

        var filelistPath = Path.Combine(Path.GetTempPath(), "RepointPakFilelist.txt");
        var lines = new List<string>(staged.Count);
        foreach (var (src, dest) in staged)
        {
            var rel = Path.GetRelativePath(pakWd, src);
            if (Path.IsPathRooted(rel)) rel = src;
            rel = rel.Replace('\\', '/');
            string canonical;
            try { canonical = Path.GetFullPath(src); } catch { canonical = src; }
            if (canonical.Length == 260) { Console.WriteLine("dead-zone source path: " + canonical); return 1; }
            lines.Add($"\"{rel}\" \"{dest}\"");
        }
        File.WriteAllText(filelistPath, string.Join("\n", lines));
        Console.WriteLine("filelist:");
        foreach (var l in lines) Console.WriteLine("  " + l);

        if (File.Exists(pakPath)) File.Delete(pakPath);

        var psi = new ProcessStartInfo
        {
            FileName = pakExe,
            Arguments = $"\"{pakPath}\" -create=\"{filelistPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = pakWd
        };
        using var proc = Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();

        Console.WriteLine(stdout);
        if (!string.IsNullOrWhiteSpace(stderr)) Console.WriteLine(stderr);
        Console.WriteLine("exit code: " + proc.ExitCode);

        if (proc.ExitCode != 0)
        {
            Console.WriteLine("UnrealPak failed");
            return 1;
        }

        var skipped = new List<string>();
        foreach (Match m in Regex.Matches(stdout + "\n" + stderr, "(?:Missing file|Unable to create file) \"([^\"]+)\""))
            if (!skipped.Contains(m.Groups[1].Value)) skipped.Add(m.Groups[1].Value);
        var addedMatch = Regex.Match(stdout, @"Added (\d+) files, \d+ bytes total");
        int uniqueDests = staged.Select(e => e.dest).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        int added = addedMatch.Success ? int.Parse(addedMatch.Groups[1].Value) : -1;
        if (skipped.Count > 0 || !addedMatch.Success || added != uniqueDests)
        {
            Console.WriteLine($"incomplete pak: Added={added} expected={uniqueDests} skipped={skipped.Count}");
            foreach (var s in skipped) Console.WriteLine("  skipped: " + s);
            try { if (File.Exists(pakPath)) File.Delete(pakPath); } catch { }
            return 1;
        }

        var sigSource = FindSig(pakExe, contentRoot);
        if (sigSource != null)
        {
            File.Copy(sigSource, Path.ChangeExtension(pakPath, ".sig"), true);
            Console.WriteLine("sig: " + sigSource);
        }
        else Console.WriteLine("sig: not found (pak will be unsigned)");

        Console.WriteLine();
        Console.WriteLine("PAK READY: " + pakPath);
        if (File.Exists(Path.ChangeExtension(pakPath, ".sig"))) Console.WriteLine("SIG READY: " + Path.ChangeExtension(pakPath, ".sig"));
        return 0;
    }

    static int ProcessStaged(string stagedUasset, string targetGamePath, string targetRel, bool comboFile, bool includeCanary)
    {
        var pre = Analyze(stagedUasset);
        var patchables = comboFile ? pre.Fields.Where(c => c.IsBarehandPatch).ToList() : new List<ComboField>();
        var weapons = comboFile ? pre.Fields.Where(c => c.IsComboProp && !c.IsBarehandPatch).ToList() : new List<ComboField>();
        if (comboFile && patchables.Count == 0) { Console.WriteLine("  no barehand combo field found - aborting"); return -1; }

        foreach (var c in pre.Fields)
            Console.WriteLine($"  {(c.IsBarehandPatch ? "PATCH" : "keep ")} {c.PropName} @ {c.Path} -> {c.Target}");

        int rewired = PatchComboProps(stagedUasset, targetGamePath, patchables.Select(c => c.Path).ToHashSet(), includeCanary, out int canarySet);
        if (comboFile && rewired != patchables.Count)
        {
            Console.WriteLine($"  PATCH COUNT MISMATCH: rewired {rewired}, expected {patchables.Count}");
            return -1;
        }
        if (rewired == 0 && canarySet == 0)
        {
            Console.WriteLine("  unchanged - not staged");
            return 0;
        }

        var post = Analyze(stagedUasset);
        var postByPath = post.Fields.ToDictionary(c => c.Path, c => c);
        var expectedSuffix = " " + Path.GetFileName(targetRel) + " (outer=" + targetGamePath + ")";

        foreach (var c in pre.Fields)
        {
            if (!postByPath.TryGetValue(c.Path, out var q))
            {
                Console.WriteLine($"  VERIFY FAILED: field lost: {c.Path}");
                return -1;
            }
            if (c.IsBarehandPatch)
            {
                if (!q.Target.EndsWith(expectedSuffix))
                {
                    Console.WriteLine($"  VERIFY FAILED: {c.Path} -> {q.Target} (expected *{expectedSuffix})");
                    return -1;
                }
            }
            else if (q.Target != c.Target)
            {
                Console.WriteLine($"  VERIFY FAILED: weapon field changed: {c.Path}: {c.Target} -> {q.Target}");
                return -1;
            }
        }

        var postHealth = CollectHealth(stagedUasset);
        if (includeCanary && pre.Health.Count > 0)
        {
            if (postHealth.Count != pre.Health.Count || postHealth.Any(v => Math.Abs(v - CanaryFields[0].Value) > 1e-6f))
            {
                Console.WriteLine($"  VERIFY FAILED: m_fHealth post = [{string.Join(", ", postHealth)}] (expected {CanaryFields.Length}x {CanaryFields[0].Value})");
                return -1;
            }
        }
        else if (!includeCanary)
        {
            if (postHealth.Count != pre.Health.Count || pre.Health.Where((v, i) => Math.Abs(postHealth[i] - v) > 1e-6f).Any())
            {
                Console.WriteLine("  VERIFY FAILED: m_fHealth changed without request");
                return -1;
            }
        }

        if (comboFile)
            Console.WriteLine($"  ok: {rewired} barehand field(s) -> {Path.GetFileName(targetRel)}, {weapons.Count} weapon field(s) untouched, health canary {canarySet} set");
        else
            Console.WriteLine($"  ok: health canary {canarySet} set (was {string.Join("/", pre.Health)})");
        return 1;
    }

    sealed class Analysis
    {
        public List<ComboField> Fields = new();
        public List<float> Health = new();
    }

    static Analysis Analyze(string uassetPath)
    {
        var asset = new UAsset(uassetPath, Eng, null, CustomSerializationFlags.None);
        var result = new Analysis();
        foreach (var exp in asset.Exports)
        {
            if (exp is not NormalExport ne || ne.Data == null) continue;
            WalkProps(ne.Data, "", (p, path) =>
            {
                if (p is ObjectPropertyData op)
                {
                    var name = op.Name.Value?.ToString() ?? "";
                    if (name is not ("m_Combo" or "m_ComboOverride")) return;
                    var target = ResolveImport(asset, op.Value);
                    result.Fields.Add(new ComboField
                    {
                        Path = path,
                        PropName = name,
                        Target = target,
                        InOverrides = path.Contains("m_ComboOverrides"),
                        WeaponTarget = IsWeaponTarget(target),
                    });
                }
                else if (p is FloatPropertyData fp)
                {
                    if (fp.Name.Value?.ToString() == CanaryFields[0].Name) result.Health.Add(fp.Value);
                }
            });
        }
        return result;
    }

    static List<float> CollectHealth(string uassetPath)
    {
        var asset = new UAsset(uassetPath, Eng, null, CustomSerializationFlags.None);
        var result = new List<float>();
        foreach (var exp in asset.Exports)
        {
            if (exp is not NormalExport ne || ne.Data == null) continue;
            WalkProps(ne.Data, "", (p, _) =>
            {
                if (p is FloatPropertyData fp && fp.Name.Value?.ToString() == CanaryFields[0].Name)
                    result.Add(fp.Value);
            });
        }
        return result;
    }

    static void CopyAsset(string contentRoot, string stagingContent, string rel)
    {
        var src = Path.Combine(contentRoot, rel.Replace('/', Path.DirectorySeparatorChar) + ".uasset");
        if (!File.Exists(src)) throw new FileNotFoundException(src);
        var dst = Path.Combine(stagingContent, rel.Replace('/', Path.DirectorySeparatorChar) + ".uasset");
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        File.Copy(src, dst, true);
        var srcUexp = Path.ChangeExtension(src, ".uexp");
        if (File.Exists(srcUexp)) File.Copy(srcUexp, Path.ChangeExtension(dst, ".uexp"), true);
    }

    static void AddStaged(List<(string src, string dest)> staged, string stagingContent, string rel)
    {
        var uasset = Path.Combine(stagingContent, rel.Replace('/', Path.DirectorySeparatorChar) + ".uasset");
        staged.Add((uasset, $"../../../Sifu/Content/{rel}.uasset"));
        var uexp = Path.ChangeExtension(uasset, ".uexp");
        if (File.Exists(uexp)) staged.Add((uexp, $"../../../Sifu/Content/{rel}.uexp"));
    }

    static int PatchComboProps(string uassetPath, string targetGamePath, HashSet<string> patchPaths, bool includeCanary, out int canarySet)
    {
        var asset = new UAsset(uassetPath, Eng, null, CustomSerializationFlags.None);
        string objName = Path.GetFileName(targetGamePath);

        string classPackage = "/Script/Sifu";
        string className = "Combo";
        foreach (var exp in asset.Exports)
        {
            if (exp is not NormalExport ne || ne.Data == null) continue;
            bool found = false;
            WalkProps(ne.Data, "", (p, path) =>
            {
                if (found || p is not ObjectPropertyData op) return;
                if (op.Name.Value?.ToString() is not ("m_Combo" or "m_ComboOverride")) return;
                if (op.Value == null || !op.Value.IsImport()) return;
                int i = -(op.Value.Index + 1);
                if (i < 0 || i >= asset.Imports.Count) return;
                classPackage = asset.Imports[i].ClassPackage.Value?.ToString() ?? classPackage;
                className = asset.Imports[i].ClassName.Value?.ToString() ?? className;
                found = true;
            });
            if (found) break;
        }

        int pkgIdx = -1;
        for (int i = 0; i < asset.Imports.Count; i++)
        {
            var imp = asset.Imports[i];
            if (imp.ClassName.Value?.ToString() == "Package" && imp.ObjectName.Value?.ToString() == targetGamePath)
            { pkgIdx = i; break; }
        }
        if (pkgIdx < 0)
        {
            pkgIdx = asset.Imports.Count;
            asset.Imports.Add(new UAssetAPI.Import
            {
                ClassPackage = FName.FromString(asset, "/Script/CoreUObject"),
                ClassName = FName.FromString(asset, "Package"),
                ObjectName = FName.FromString(asset, targetGamePath),
                OuterIndex = new FPackageIndex(0),
                PackageName = FName.FromString(asset, "None")
            });
        }

        int objIdx = -1;
        for (int i = 0; i < asset.Imports.Count; i++)
        {
            var imp = asset.Imports[i];
            if (imp.ObjectName.Value?.ToString() == objName && imp.OuterIndex.Index == -(pkgIdx + 1))
            { objIdx = i; break; }
        }
        if (objIdx < 0)
        {
            objIdx = asset.Imports.Count;
            asset.Imports.Add(new UAssetAPI.Import
            {
                ClassPackage = FName.FromString(asset, classPackage),
                ClassName = FName.FromString(asset, className),
                ObjectName = FName.FromString(asset, objName),
                OuterIndex = FPackageIndex.FromImport(pkgIdx),
                PackageName = FName.FromString(asset, "None")
            });
        }

        int n = 0;
        int canaryLocal = 0;
        foreach (var exp in asset.Exports)
        {
            if (exp is not NormalExport ne || ne.Data == null) continue;
            WalkProps(ne.Data, "", (p, path) =>
            {
                if (p is ObjectPropertyData op && patchPaths.Contains(path))
                {
                    op.Value = FPackageIndex.FromImport(objIdx);
                    n++;
                }
                else if (includeCanary && p is FloatPropertyData fp && fp.Name.Value?.ToString() == CanaryFields[0].Name)
                {
                    fp.Value = CanaryFields[0].Value;
                    canaryLocal++;
                }
            });
        }
        canarySet = canaryLocal;

        if (n > 0 || canarySet > 0) asset.Write(uassetPath);
        return n;
    }

    static int StageMeshScenarios(string contentRoot, string stagingContent, List<(string src, string dest)> staged)
    {
        var existing = new HashSet<string>(staged.Select(s => s.dest), StringComparer.OrdinalIgnoreCase);
        var sources = new List<(string root, string rel)>();

        foreach (var dirRel in MeshScenarioDbDirs)
        {
            var dir = Path.Combine(contentRoot, dirRel.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(dir)) { Console.WriteLine("missing scenario dir: " + dir); return -1; }
            foreach (var f in Directory.GetFiles(dir, "*Scenario*.uasset", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(contentRoot, f).Replace('\\', '/');
                if (rel.EndsWith(".uasset")) rel = rel.Substring(0, rel.Length - ".uasset".Length);
                sources.Add((contentRoot, rel));
            }
        }

        var bpContent = ExtractBlueprintScenarios(contentRoot);
        if (bpContent == null) return -1;
        foreach (var f in Directory.GetFiles(bpContent, "*Scenario*.uasset", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(bpContent, f).Replace('\\', '/');
            if (rel.EndsWith(".uasset")) rel = rel.Substring(0, rel.Length - ".uasset".Length);
            if (!IsMeshScenarioRel(rel)) continue;
            sources.Add((bpContent, rel));
        }

        if (sources.Count == 0) { Console.WriteLine("no mesh scenario source found"); return -1; }
        Console.WriteLine($"mesh sources: {sources.Count} scenario file(s)");

        int ok = 0, noField = 0;
        foreach (var (root, rel) in sources)
        {
            var dest = $"../../../Sifu/Content/{rel}.uasset";
            if (existing.Contains(dest)) { Console.WriteLine($"already staged (combo): {rel}"); continue; }

            var srcUasset = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar) + ".uasset");
            if (!File.Exists(srcUasset)) { Console.WriteLine("missing scenario: " + srcUasset); return -1; }
            var stagedUasset = Path.Combine(stagingContent, rel.Replace('/', Path.DirectorySeparatorChar) + ".uasset");
            Directory.CreateDirectory(Path.GetDirectoryName(stagedUasset)!);
            File.Copy(srcUasset, stagedUasset, true);
            var srcUexp = Path.ChangeExtension(srcUasset, ".uexp");
            if (File.Exists(srcUexp)) File.Copy(srcUexp, Path.ChangeExtension(stagedUasset, ".uexp"), true);

            Console.WriteLine($"=== {rel} (mesh)");
            int n = PatchScenarioMesh(stagedUasset);
            if (n < 0) return -1;
            if (n == 0)
            {
                Console.WriteLine("  no m_OverrideSkeletalMesh - not staged");
                try { File.Delete(stagedUasset); } catch { }
                try { var u = Path.ChangeExtension(stagedUasset, ".uexp"); if (File.Exists(u)) File.Delete(u); } catch { }
                noField++;
                continue;
            }
            AddStaged(staged, stagingContent, rel);
            existing.Add(dest);
            ok++;
        }

        Console.WriteLine($"mesh: {ok} scenario file(s) staged, {noField} without mesh field");
        if (ok == 0) { Console.WriteLine("no mesh scenario staged"); return -1; }
        return 0;
    }

    static bool IsMeshScenarioRel(string rel)
    {
        if (rel.StartsWith("Blueprints/TrainingRoom/AI/Archetypes/Grunt/", StringComparison.OrdinalIgnoreCase)) return true;
        if (rel.StartsWith("Blueprints/TrainingRoom/AI/Archetypes/Servant/", StringComparison.OrdinalIgnoreCase)) return true;
        if (rel.Equals("Blueprints/TrainingRoom/AI/TrainingAI_Scenario", StringComparison.OrdinalIgnoreCase)) return true;
        if (rel.Equals("Blueprints/Tutorial/AI/TutorialGrunt_Scenario", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    static int PatchScenarioMesh(string stagedUasset)
    {
        var asset = new UAsset(stagedUasset, Eng, null, CustomSerializationFlags.None);
        var patch = new List<(string Path, ObjectPropertyData Prop)>();
        foreach (var exp in asset.Exports)
        {
            if (exp is not NormalExport ne || ne.Data == null) continue;
            WalkProps(ne.Data, "", (p, path) =>
            {
                if (p is ObjectPropertyData op && op.Name.Value?.ToString() == "m_OverrideSkeletalMesh")
                    patch.Add((path, op));
            });
        }
        if (patch.Count == 0) return 0;

        string classPackage = "/Script/Engine";
        string className = "SkeletalMesh";
        foreach (var (_, op) in patch)
        {
            if (op.Value == null || !op.Value.IsImport()) continue;
            int i = -(op.Value.Index + 1);
            if (i < 0 || i >= asset.Imports.Count) continue;
            classPackage = asset.Imports[i].ClassPackage.Value?.ToString() ?? classPackage;
            className = asset.Imports[i].ClassName.Value?.ToString() ?? className;
            break;
        }

        int pkgIdx = -1;
        for (int i = 0; i < asset.Imports.Count; i++)
        {
            var imp = asset.Imports[i];
            if (imp.ClassName.Value?.ToString() == "Package" && imp.ObjectName.Value?.ToString() == YangMeshGamePath)
            { pkgIdx = i; break; }
        }
        if (pkgIdx < 0)
        {
            pkgIdx = asset.Imports.Count;
            asset.Imports.Add(new UAssetAPI.Import
            {
                ClassPackage = FName.FromString(asset, "/Script/CoreUObject"),
                ClassName = FName.FromString(asset, "Package"),
                ObjectName = FName.FromString(asset, YangMeshGamePath),
                OuterIndex = new FPackageIndex(0),
                PackageName = FName.FromString(asset, "None")
            });
        }

        int objIdx = -1;
        for (int i = 0; i < asset.Imports.Count; i++)
        {
            var imp = asset.Imports[i];
            if (imp.ObjectName.Value?.ToString() == YangMeshObjectName && imp.OuterIndex.Index == -(pkgIdx + 1))
            { objIdx = i; break; }
        }
        if (objIdx < 0)
        {
            objIdx = asset.Imports.Count;
            asset.Imports.Add(new UAssetAPI.Import
            {
                ClassPackage = FName.FromString(asset, classPackage),
                ClassName = FName.FromString(asset, className),
                ObjectName = FName.FromString(asset, YangMeshObjectName),
                OuterIndex = FPackageIndex.FromImport(pkgIdx),
                PackageName = FName.FromString(asset, "None")
            });
        }

        foreach (var (path, op) in patch)
        {
            Console.WriteLine($"  PATCH m_OverrideSkeletalMesh @ {path} -> {YangMeshObjectName}");
            op.Value = FPackageIndex.FromImport(objIdx);
        }
        asset.Write(stagedUasset);

        var post = new UAsset(stagedUasset, Eng, null, CustomSerializationFlags.None);
        var expectedSuffix = " " + YangMeshObjectName + " (outer=" + YangMeshGamePath + ")";
        int n = 0;
        bool verifyFailed = false;
        foreach (var exp in post.Exports)
        {
            if (exp is not NormalExport ne || ne.Data == null) continue;
            WalkProps(ne.Data, "", (p, path) =>
            {
                if (p is not ObjectPropertyData op || op.Name.Value?.ToString() != "m_OverrideSkeletalMesh") return;
                n++;
                var target = ResolveImport(post, op.Value);
                if (!target.EndsWith(expectedSuffix))
                {
                    Console.WriteLine($"  VERIFY FAILED: {path} -> {target} (expected *{expectedSuffix})");
                    verifyFailed = true;
                }
            });
        }
        if (n != patch.Count || n == 0 || verifyFailed)
        {
            Console.WriteLine($"  VERIFY FAILED: {n} mesh field(s) after write, expected {patch.Count}");
            return -1;
        }
        Console.WriteLine($"  ok: {n} mesh field(s) -> {YangMeshObjectName}");
        return n;
    }

    static string? ExtractBlueprintScenarios(string contentRoot)
    {
        var stage = Path.Combine(Path.GetTempPath(), "SifuPoCC_BP");
        if (Directory.Exists(stage)) Directory.Delete(stage, true);
        Directory.CreateDirectory(stage);

        if (!TryFindUnrealPak(out var pakExe, out var pakWd))
        {
            Console.WriteLine("UnrealPak.exe not found (expected at <repo>\\tools\\ue4\\UnrealPak\\UnrealPak.exe)");
            return null;
        }
        if (!File.Exists(OriginalPakPath)) { Console.WriteLine("original pak missing: " + OriginalPakPath); return null; }
        var crypto = FindCryptoJson(pakExe, contentRoot);
        if (crypto == null) { Console.WriteLine("Crypto.json not found (checked app dir, content root, UnrealPak dir)"); return null; }

        foreach (var filter in BlueprintScenarioFilters)
        {
            var psi = new ProcessStartInfo
            {
                FileName = pakExe,
                Arguments = $"\"{OriginalPakPath}\" -CryptoKeys=\"{crypto}\" -Extract \"{stage}\" -Filter=\"{filter}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = pakWd
            };
            using var proc = Process.Start(psi)!;
            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            Console.WriteLine($"extract {filter}: exit {proc.ExitCode}");
            if (proc.ExitCode != 0)
            {
                Console.WriteLine(stdout);
                if (!string.IsNullOrWhiteSpace(stderr)) Console.WriteLine(stderr);
                return null;
            }
        }

        var contentDir = Path.Combine(stage, "Sifu", "Content");
        if (!Directory.Exists(contentDir)) { Console.WriteLine("extract produced no Content tree: " + stage); return null; }
        Console.WriteLine("blueprint scenarios extracted to " + stage);
        return contentDir;
    }

    static string? FindCryptoJson(string pakExe, string contentRoot)
    {
        var candidates = new List<string>
        {
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Crypto.json"),
            Path.Combine(Path.GetDirectoryName(pakExe)!, "Crypto.json"),
        };
        var walk = new DirectoryInfo(Path.GetFullPath(contentRoot));
        for (int i = 0; i < 8 && walk != null; i++)
        {
            candidates.Add(Path.Combine(walk.FullName, "Crypto.json"));
            walk = walk.Parent;
        }
        foreach (var c in candidates)
            if (File.Exists(c)) return Path.GetFullPath(c);
        return null;
    }

    static bool IsWeaponTarget(string target)
    {
        var m = Regex.Match(target, @"^\S+\s+(\S+?)(?:\s+\(outer=|$)");
        var objectName = m.Success ? m.Groups[1].Value : target;
        return WeaponTargetRe.IsMatch(objectName);
    }

    static string ResolveImport(UAsset asset, FPackageIndex? idx)
    {
        if (idx == null || idx.Index >= 0) return idx == null ? "<null>" : $"<export {idx.Index}>";
        int i = -(idx.Index + 1);
        if (i < 0 || i >= asset.Imports.Count) return $"<out-of-range {idx.Index}>";
        var imp = asset.Imports[i];
        int o = imp.OuterIndex.Index;
        var outer = o < 0 && -(o + 1) < asset.Imports.Count
            ? asset.Imports[-(o + 1)].ObjectName.Value?.ToString()
            : (o == 0 ? "" : $"<{o}>");
        return $"{imp.ClassName.Value} {imp.ObjectName.Value} (outer={outer})";
    }

    static void WalkProps(IEnumerable<PropertyData> props, string prefix, Action<PropertyData, string> onProp)
    {
        foreach (var p in props)
        {
            if (p == null) continue;
            var name = p.Name.Value?.ToString() ?? "?";
            var path = prefix.Length == 0 ? name : prefix + "." + name;
            onProp(p, path);
            if (p is StructPropertyData sp && sp.Value != null)
                WalkProps(sp.Value, path, onProp);
            else if (p is ArrayPropertyData ap && ap.Value != null)
            {
                for (int i = 0; i < ap.Value.Length; i++)
                {
                    var e = ap.Value[i];
                    if (e == null) continue;
                    var ep = path + "[" + i + "]";
                    onProp(e, ep);
                    if (e is StructPropertyData esp && esp.Value != null)
                        WalkProps(esp.Value, ep, onProp);
                }
            }
        }
    }

    static bool TryFindUnrealPak(out string pakExe, out string pakWd)
    {
        pakExe = "";
        pakWd = "";
        var walk = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        while (walk != null)
        {
            var p = Path.Combine(walk.FullName, "tools", "ue4", "UnrealPak", "UnrealPak.exe");
            if (File.Exists(p)) { pakExe = p; pakWd = Path.GetDirectoryName(p)!; return true; }
            walk = walk.Parent;
        }
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var p = Path.Combine(dir.Trim(), "UnrealPak.exe");
            if (File.Exists(p)) { pakExe = p; pakWd = Path.GetDirectoryName(p)!; return true; }
        }
        return false;
    }

    static string? FindSig(string pakExe, string contentRoot)
    {
        var candidates = new List<string>
        {
            Path.Combine(contentRoot, "Paks", "pakchunk0-WindowsNoEditor.sig"),
            @"C:\Program Files\Epic Games\Sifu\Sifu\Content\Paks\pakchunk0-WindowsNoEditor.sig",
            Path.Combine(Path.GetDirectoryName(pakExe)!, "pakchunk0-WindowsNoEditor.sig"),
        };
        foreach (var root in new[] { AppDomain.CurrentDomain.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var walk = new DirectoryInfo(root);
            while (walk != null)
            {
                candidates.Add(Path.Combine(walk.FullName, "Content", "Paks", "pakchunk0-WindowsNoEditor.sig"));
                if (walk.Name == "SifuMovesetEditor") break;
                walk = walk.Parent;
            }
        }
        var walk2 = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        while (walk2 != null)
        {
            foreach (var rel in new[]
            {
                Path.Combine("bin", "Debug", "net10.0-windows", "tools", "ue4", "UnrealPak", "pakchunk0-WindowsNoEditor.sig"),
                Path.Combine("bin", "Release", "net10.0-windows", "tools", "ue4", "UnrealPak", "pakchunk0-WindowsNoEditor.sig"),
                Path.Combine("publish", "tools", "ue4", "UnrealPak", "pakchunk0-WindowsNoEditor.sig"),
            })
            {
                candidates.Add(Path.Combine(walk2.FullName, rel));
            }
            if (walk2.Name == "SifuMovesetEditor") break;
            walk2 = walk2.Parent;
        }
        foreach (var c in candidates)
        {
            if (File.Exists(c)) return c;
        }
        return null;
    }
}
