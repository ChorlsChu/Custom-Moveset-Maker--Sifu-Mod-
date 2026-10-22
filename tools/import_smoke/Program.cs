using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using SifuMovesetEditor;
using SifuMovesetEditor.Setup;
using SifuMovesetEditor.Update;
using System.Windows.Documents;
using WpfRun = System.Windows.Documents.Run;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;

namespace import_smoke;

/// <summary>
/// Smoke tests for the modded-combo import diff: a vanilla combo file is copied,
/// mutated with UAssetAPI (chance, condition clear, condition param, start entry,
/// AI payload, appended AI node), then run through ReadModdedComboNodes /
/// ReadModdedStartEntries / ApplyModdedComboToVanilla against a fresh vanilla parse
/// of the same tree. Every mutation must surface as the right override/count on the
/// vanilla graph. Exit code 0 = all passed.
/// </summary>
internal static class Program
{
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadLibrary(string lpFileName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeLibrary(IntPtr hModule);

    private static readonly string ContentRoot =
        Environment.GetEnvironmentVariable("SIFU_CONTENT")
        ?? @"C:\Users\Charles\Downloads\Sifu Modding\Unreal Pak Extracter and Creator\extractedPaks\pakchunk0-WindowsNoEditor\Sifu\Content";

    private static readonly string SeanP1Rel = "Game/DB/AI/Archetypes/Sean/Sean_Combo_Phase1";
    private static readonly string FajarP2Rel = "Game/DB/AI/Archetypes/Fajar/Attacks/Fajar_Combo_P2";

    // Real mod paks for the surfaced-node / phantom-card regressions (Bug 1-3):
    // skipped when absent so the smoke stays runnable without the user's downloads.
    private static readonly string EHPak =
        @"C:\Users\Charles\Downloads\Sifu Modding\Movesets Prototypes\Moveset Maker\SifuMovesetEditor\bin\Debug\net10.0-windows\ExportedMods\EnemiesHarder_V2.pak";
    private static readonly string WWPak =
        @"C:\Users\Charles\Downloads\Sifu Modding\WW Test mod\.1Wude Wakening.pak";

    private static readonly string OutDir = Path.Combine(Path.GetTempPath(), "import_smoke");
    private static readonly List<string> Failures = new();

    private static readonly MethodInfo MemberwiseCloneMethod =
        typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static int Main()
    {
        ErrorLog.Init();
        Directory.CreateDirectory(OutDir);

        string seanP1 = Path.Combine(ContentRoot, "DB", "AI", "Archetypes", "Sean", "Sean_Combo_Phase1.uasset");
        string fajarP2 = Path.Combine(ContentRoot, "DB", "AI", "Archetypes", "Fajar", "Attacks", "Fajar_Combo_P2.uasset");
        if (!File.Exists(seanP1) || !File.Exists(fajarP2))
        {
            Console.Error.WriteLine($"test assets missing under {ContentRoot} (set SIFU_CONTENT to the game Content dir)");
            return 2;
        }

        Run("ReaderBasics", TestReaderBasics);
        Run("ChanceOverride", TestChanceOverride);
        Run("ConditionClear", TestConditionClear);
        Run("ConditionParam", TestConditionParam);
        Run("StartEntryProbability", TestStartEntryProbability);
        Run("AiPayloadEventToAttack", TestAiPayloadEventToAttack);
        Run("SurfacedEventNode", TestSurfacedEventNode);
        Run("UpdateChecker", TestUpdateChecker);
        Run("UpdateSwap", TestUpdateSwap);
        Run("NoteMarkdown", TestNoteMarkdown);
        Run("RealPakEnemiesHarder", () => RunRealPak("EH", EHPak));
        Run("RealPakWudeWakening", () => RunRealPak("WW", WWPak));

        Console.WriteLine();
        if (Failures.Count == 0)
        {
            Console.WriteLine("ALL IMPORT SMOKE TESTS PASSED");
            return 0;
        }
        Console.WriteLine($"{Failures.Count} FAILED:");
        foreach (var f in Failures) Console.WriteLine("  " + f);
        return 1;
    }

    private static void Run(string name, Action test)
    {
        try
        {
            test();
            Console.WriteLine($"  PASS {name}");
        }
        catch (Exception ex)
        {
            Failures.Add($"{name}: {ex.Message}");
            Console.WriteLine($"  FAIL {name}: {ex.Message}");
            Console.WriteLine(LogTail());
        }
    }

    private static void Expect(bool cond, string msg)
    {
        if (!cond) throw new Exception(msg);
    }

    private static string LogTail()
    {
        try
        {
            // Logs live in logs\error-<date>.log (rotated, 7-day history); slice from
            // the last session banner so the tail shows only this run. Falls back to
            // the legacy error.log path for old sessions.
            string dir = Path.Combine(Directory.GetCurrentDirectory(), "logs");
            string path = Directory.Exists(dir)
                ? Directory.GetFiles(dir, "error-*.log")
                    .OrderByDescending(f => File.GetLastWriteTimeUtc(f))
                    .FirstOrDefault() ?? ""
                : "";
            if (path.Length == 0)
                path = Path.Combine(Directory.GetCurrentDirectory(), "error.log");
            if (!File.Exists(path)) return "";
            var lines = File.ReadAllLines(path);
            int start = 0;
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                if (lines[i].Contains("===== session start")) { start = i; break; }
            }
            var tail = lines.Skip(Math.Max(start, lines.Length - 30));
            return $"---- {Path.GetFileName(path)} tail ----\n" + string.Join("\n", tail);
        }
        catch { return ""; }
    }

    // ---------------------------------------------------------------- update notes

    private static void TestNoteMarkdown()
    {
        // GitHub release notes use **bold** / _italic_ - the dialog must strip
        // the markers and wrap the text, not print the symbols.
        var bold = MarkdownInlines.Render("**New Features**");
        Expect(bold.Count == 1, $"bold block count ({bold.Count})");
        Expect(bold[0] is Bold, "single bold inline");
        Expect(((Bold)bold[0]).Inlines.FirstOrDefault() is WpfRun b1 && b1.Text == "New Features",
            "bold text without stars");

        var mixed = MarkdownInlines.Render("Plain **bold** and _italic_ end");
        Expect(mixed.Count == 5, $"mixed inline count ({mixed.Count})");
        Expect(mixed[1] is Bold && mixed[3] is Italic, "mixed bold/italic kinds");

        var starItal = MarkdownInlines.Render("a *b* c");
        Expect(starItal.Count == 3 && starItal[1] is Italic, "star italic");

        var literal = MarkdownInlines.Render("use ** for bold");
        Expect(literal.Count == 1 && literal[0] is WpfRun lr && lr.Text == "use ** for bold",
            "unmatched markers stay literal");

        var snake = MarkdownInlines.Render("some_snake_case_word");
        Expect(snake.Count == 1 && snake[0] is WpfRun sr && sr.Text == "some_snake_case_word",
            "intraword underscores untouched");

        var multi = MarkdownInlines.Render("line1\r\n**line2**");
        Expect(multi.Count == 2 && multi[0] is WpfRun mr && mr.Text == "line1\n" && multi[1] is Bold,
            "newlines preserved as line breaks");
    }

    // ---------------------------------------------------------------- update check

    private static void TestUpdateChecker()
    {
        // Tag/version parsing: repo tags look like "v1.2"; csproj Version like "1.3".
        Expect(UpdateChecker.ParseTagVersion("v1.2") == new Version(1, 2), "ParseTagVersion(v1.2)");
        Expect(UpdateChecker.ParseTagVersion("1.3") == new Version(1, 3), "ParseTagVersion(1.3)");
        Expect(UpdateChecker.ParseTagVersion("v1.4.1") == new Version(1, 4, 1), "ParseTagVersion(v1.4.1)");
        Expect(UpdateChecker.ParseTagVersion("1.3+abc123") == new Version(1, 3), "ParseTagVersion strips +sha");
        Expect(UpdateChecker.ParseTagVersion("1.3 (deadbeef)") == new Version(1, 3), "ParseTagVersion strips (sha)");
        Expect(UpdateChecker.ParseTagVersion("garbage") == null, "ParseTagVersion(garbage) is null");
        Expect(UpdateChecker.ParseTagVersion(null) == null, "ParseTagVersion(null) is null");

        var v13 = new Version(1, 3);
        Expect(UpdateChecker.IsNewer(new Version(1, 4), v13), "1.4 > 1.3");
        Expect(UpdateChecker.IsNewer(new Version(1, 3, 1), v13), "1.3.1 > 1.3");
        Expect(!UpdateChecker.IsNewer(v13, v13), "1.3 !> 1.3");
        Expect(!UpdateChecker.IsNewer(new Version(1, 2), v13), "1.2 !> 1.3");
        // Out-of-order publish: the max version wins (1.4 beats 1.3.2).
        Expect(new Version(1, 4) > new Version(1, 3, 2), "max-version pick 1.4 > 1.3.2");

        var current = UpdateChecker.CurrentVersion();
        Expect(current > new Version(0, 0), $"CurrentVersion parses ({current})");

        // Zip payload layout: exe at the root, or inside one wrapper folder
        // (SifuMovesetEditor/) - both must resolve to the dir to copy from.
        var stage = Path.Combine(Path.GetTempPath(), "up_smoke_" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(stage);
        try
        {
            const string exe = "SifuMovesetEditor.exe";
            File.WriteAllText(Path.Combine(stage, exe), "x");
            Expect(UpdateChecker.FindPayloadDir(stage, exe) == stage, "payload resolved at zip root");

            var wrap = Path.Combine(stage, "SifuMovesetEditor");
            Directory.CreateDirectory(wrap);
            File.Delete(Path.Combine(stage, exe));
            File.WriteAllText(Path.Combine(wrap, exe), "x");
            Expect(UpdateChecker.FindPayloadDir(stage, exe) == wrap, "payload resolved in wrapper folder");

            File.Delete(Path.Combine(wrap, exe));
            var miss = false;
            try { UpdateChecker.FindPayloadDir(stage, exe); }
            catch (InvalidOperationException) { miss = true; }
            Expect(miss, "missing exe throws");
        }
        finally
        {
            try { Directory.Delete(stage, true); } catch { }
        }
    }

    /// <summary>Exercises the in-process swap in temp dirs. (1) Design gates first:
    /// Windows must allow renaming files this process genuinely holds open - our
    /// own SifuMovesetEditor.dll and exe - or the whole in-place approach is dead
    /// on this machine; and a LoadLibrary'd native dll (image section, share=READ)
    /// must report locked to IsFileLocked's write-probe. (2) A held handle with
    /// the loader's share mode (read+delete: exclusive probes report locked,
    /// rename allowed) must be renamed to *.old and replaced, settings preserved,
    /// manifest written. (3) A mid-copy failure must roll every rename back and
    /// clear the manifest. (4) CleanupSwapLeftovers must then remove the *.old
    /// leftovers.</summary>
    private static void TestUpdateSwap()
    {
        // ---- Design gate: rename files that are truly in use by this process. ----
        var selfDll = Path.Combine(AppContext.BaseDirectory, "SifuMovesetEditor.dll");
        Expect(File.Exists(selfDll), "gate: referenced assembly sits beside smoke exe");
        try
        {
            File.Move(selfDll, selfDll + ".gatetest");
            File.Move(selfDll + ".gatetest", selfDll);
        }
        catch (Exception ex)
        {
            try { if (File.Exists(selfDll + ".gatetest")) File.Move(selfDll + ".gatetest", selfDll); } catch { }
            Expect(false, $"gate: renaming a LOADED .NET assembly must work ({ex.Message})");
        }

        var exe = Environment.ProcessPath!;
        try
        {
            File.Move(exe, exe + ".gatetest");
            File.Move(exe + ".gatetest", exe);
        }
        catch (Exception ex)
        {
            try { if (File.Exists(exe + ".gatetest")) File.Move(exe + ".gatetest", exe); } catch { }
            Expect(false, $"gate: renaming the RUNNING exe must work ({ex.Message})");
        }

        // Image-section gate: a native DLL loaded via LoadLibrary closes its
        // handle but the section keeps a share=READ lock - the exact blind spot
        // that made a read-probe miss WebView2Loader.dll in the field (probe said
        // free, File.Copy then died with a sharing violation). The write-probe
        // must report such a file locked.
        var nativeDll = Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native", "WebView2Loader.dll");
        if (!File.Exists(nativeDll))
            nativeDll = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
                "bin", "Debug", "net10.0-windows", "runtimes", "win-x64", "native", "WebView2Loader.dll");
        if (File.Exists(nativeDll))
        {
            var mod = LoadLibrary(nativeDll);
            Expect(mod != IntPtr.Zero, "gate: LoadLibrary of WebView2Loader.dll");
            try
            {
                Expect(UpdateChecker.IsFileLocked(nativeDll),
                    "gate: image-section-held native DLL must report locked (write-probe)");
            }
            finally { FreeLibrary(mod); }
        }

        var baseDir = Path.Combine(Path.GetTempPath(), "upswap_" + Guid.NewGuid().ToString("N").Substring(0, 8));
        var payload = Path.Combine(baseDir, "stage", "app");
        var appDir = Path.Combine(baseDir, "installed");
        Directory.CreateDirectory(Path.Combine(payload, "sub"));
        Directory.CreateDirectory(Path.Combine(appDir, "sub"));

        File.WriteAllText(Path.Combine(payload, "app.dll"), "NEW");
        File.WriteAllText(Path.Combine(payload, "locked.dll"), "NEW");
        File.WriteAllText(Path.Combine(payload, "sub", "data.txt"), "NEWDATA");
        File.WriteAllText(Path.Combine(payload, "settings.json"), "FROM-PAYLOAD");
        File.WriteAllText(Path.Combine(appDir, "app.dll"), "OLD");
        File.WriteAllText(Path.Combine(appDir, "locked.dll"), "OLD");
        File.WriteAllText(Path.Combine(appDir, "settings.json"), "USER-SETTINGS");

        // ---- Happy path with one in-use file. ----
        using (new FileStream(Path.Combine(appDir, "locked.dll"),
            FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
        {
            UpdateChecker.SwapIntoPlace(payload, appDir, null);

            Expect(File.ReadAllText(Path.Combine(appDir, "app.dll")) == "NEW", "free dest overwritten");
            Expect(File.ReadAllText(Path.Combine(appDir, "locked.dll")) == "NEW", "locked dest replaced after rename");
            Expect(File.ReadAllText(Path.Combine(appDir, "locked.dll.old")) == "OLD", "original kept as .old");
            Expect(File.ReadAllText(Path.Combine(appDir, "settings.json")) == "USER-SETTINGS", "settings.json preserved");
            Expect(File.ReadAllText(Path.Combine(appDir, "sub", "data.txt")) == "NEWDATA", "subfolder copied");
            var manifest = Path.Combine(appDir, "logs", "swap.pending");
            Expect(File.Exists(manifest), "manifest written for next-instance cleanup");
            Expect(File.ReadAllText(manifest).Contains("locked.dll"), "manifest lists the .old file");
            Expect(File.ReadAllText(Path.Combine(appDir, "logs", "update.log")).Contains("swap ok"),
                "update.log records the swap");
        }

        UpdateChecker.CleanupSwapLeftovers(appDir);
        Expect(!File.Exists(Path.Combine(appDir, "locked.dll.old")), ".old removed by cleanup");
        Expect(!File.Exists(Path.Combine(appDir, "logs", "swap.pending")), "manifest removed once clean");

        // ---- Rollback: a dest name occupied by a directory fails the copy. ----
        var rbPayload = Path.Combine(baseDir, "stage2", "app");
        var rbApp = Path.Combine(baseDir, "installed2");
        Directory.CreateDirectory(rbPayload);
        Directory.CreateDirectory(rbApp);
        Directory.CreateDirectory(Path.Combine(rbApp, "locked")); // copy target is a DIRECTORY
        File.WriteAllText(Path.Combine(rbPayload, "aaa_newonly.txt"), "CREATE-ME");
        File.WriteAllText(Path.Combine(rbPayload, "locked"), "NEW-BLOCKED");
        File.WriteAllText(Path.Combine(rbPayload, "zzz_locked.dll"), "NEW-L");
        File.WriteAllText(Path.Combine(rbApp, "zzz_locked.dll"), "OLD-L");

        using (new FileStream(Path.Combine(rbApp, "zzz_locked.dll"),
            FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
        {
            var threw = false;
            try { UpdateChecker.SwapIntoPlace(rbPayload, rbApp, null); }
            catch (InvalidOperationException) { threw = true; }
            Expect(threw, "copy failure surfaces as a swap error");
            Expect(File.ReadAllText(Path.Combine(rbApp, "zzz_locked.dll")) == "OLD-L", "rollback restored the locked file");
            Expect(!File.Exists(Path.Combine(rbApp, "zzz_locked.dll.old")), "rollback removed the .old");
            Expect(!File.Exists(Path.Combine(rbApp, "aaa_newonly.txt")), "rollback removed files it created");
            Expect(!File.Exists(Path.Combine(rbApp, "logs", "swap.pending")), "rollback cleared the manifest");
            Expect(Directory.Exists(Path.Combine(rbApp, "locked")), "blocked directory untouched");
        }

        try { Directory.Delete(baseDir, true); } catch { }
    }

    // ---------------------------------------------------------------- pipeline

    private static AnimationParser NewParser()
    {
        var parser = new AnimationParser();
        string gameRoot = Path.GetDirectoryName(ContentRoot.TrimEnd(Path.DirectorySeparatorChar))!;
        parser.Initialize(gameRoot, ContentRoot);
        return parser;
    }

    private static (ModdedApplyResult result, ComboGraph graph) RunImport(
        string moddedPath, string gamePath, string weapon, bool withStartEntries = true)
    {
        var parser = NewParser();
        var nodes = parser.ReadModdedComboNodes(moddedPath, Path.GetDirectoryName(moddedPath));
        Expect(nodes.Count > 0, $"no nodes parsed from {Path.GetFileName(moddedPath)}");
        var graph = parser.LoadComboTreeFromPath(gamePath, weapon)
            ?? throw new Exception($"vanilla graph null for {gamePath}");
        var starts = withStartEntries ? parser.ReadModdedStartEntries(moddedPath) : null;
        var result = parser.ApplyModdedComboToVanilla(graph, nodes, weapon, starts);
        return (result, graph);
    }

    /// <summary>Copies the full cooked family (.uasset+.uexp+...) into a _src subfolder
    /// (so loads see payloads) and returns a CLEAN write path at the OutDir root -
    /// writing next to the copied .uexp would leave a stale sidecar shadowing the new
    /// .uasset on re-read.</summary>
    private static (string source, string written) CopyToTemp(string srcFile, string label)
    {
        string stem = Path.GetFileNameWithoutExtension(srcFile);
        string srcDir = Path.Combine(OutDir, $"{stem}_{label}_src");
        Directory.CreateDirectory(srcDir);
        foreach (var ext in new[] { ".uasset", ".uexp", ".ubulk", ".uptnl" })
        {
            string s = Path.Combine(Path.GetDirectoryName(srcFile)!, stem + ext);
            if (File.Exists(s))
                File.Copy(s, Path.Combine(srcDir, stem + ext), true);
        }
        return (Path.Combine(srcDir, stem + ".uasset"), Path.Combine(OutDir, $"{stem}_{label}.uasset"));
    }

    private static UAsset Load(string path) =>
        new(path, EngineVersion.VER_UE4_26, null, CustomSerializationFlags.None);

    private static (NormalExport combo, ArrayPropertyData nodes) ComboOf(UAsset asset)
    {
        foreach (var exp in asset.Exports)
        {
            if (exp is not NormalExport ne || ne.Data == null) continue;
            var nodes = ne.Data.OfType<ArrayPropertyData>()
                .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_Nodes");
            if (nodes != null) return (ne, nodes);
        }
        throw new Exception("no combo export with m_Nodes");
    }

    private static ArrayPropertyData? StartContainerOf(UAsset asset)
    {
        foreach (var exp in asset.Exports)
        {
            if (exp is not NormalExport ne || ne.Data == null) continue;
            var cont = ne.Data.OfType<ArrayPropertyData>()
                .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_ComboStartNodesContainer");
            if (cont != null) return cont;
        }
        return null;
    }

    private static ArrayPropertyData? TransitionsOf(StructPropertyData nodeSp) =>
        nodeSp.Value?.OfType<StructPropertyData>()
            .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_Transitions")?.Value
            ?.OfType<ArrayPropertyData>()
            .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_Transitions");

    private static int CondIndexOf(StructPropertyData rowSp) =>
        rowSp.Value?.OfType<ObjectPropertyData>()
            .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_ConditionInstance")?.Value?.Index ?? 0;

    private static string? NameOf(StructPropertyData sp) =>
        sp.Value?.OfType<NamePropertyData>()
            .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_Name")?.Value?.Value?.ToString();

    private static PropertyData CloneProp(PropertyData src)
    {
        var clone = (PropertyData)MemberwiseCloneMethod.Invoke(src, null)!;
        if (clone is StructPropertyData ss && ss.Value != null)
        {
            var list = new List<PropertyData>(ss.Value.Count);
            foreach (var c in ss.Value) list.Add(CloneProp(c));
            ss.Value = list;
        }
        else if (clone is ArrayPropertyData aa && aa.Value != null)
        {
            var arr = new PropertyData[aa.Value.Length];
            for (int i = 0; i < aa.Value.Length; i++) arr[i] = CloneProp(aa.Value[i]);
            aa.Value = arr;
        }
        else if (clone is MapPropertyData mm && mm.Value != null)
        {
            var tm = new TMap<PropertyData, PropertyData>();
            foreach (var kvp in mm.Value)
                tm.Add(CloneProp(kvp.Key), CloneProp(kvp.Value));
            mm.Value = tm;
        }
        return clone;
    }

    // ---------------------------------------------------------------- finders

    /// <summary>First non-100 probability row on a MATCHED node (attack node with a real
    /// anim path, or an AI action node with a resolved class import - both pair into
    /// groupToModded; redirect/conduit structs do not).</summary>
    private static (int tree, int row, FloatPropertyData prop) FindNon100ProbRow(ArrayPropertyData nodes)
    {
        for (int i = 0; i < nodes.Value.Length; i++)
        {
            if (nodes.Value[i] is not StructPropertyData sp || sp.Value == null) continue;
            if (sp.Value.OfType<IntPropertyData>()
                    .Any(p => p.Name?.Value?.ToString() == "m_NodeRedirect" && p.Value >= 0))
                continue;
            bool hasAnim = sp.Value.OfType<StructPropertyData>()
                .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_AttackInfos")?.Value
                ?.OfType<NamePropertyData>()
                .Any(p => p.Name?.Value?.ToString() == "m_Attacks"
                       && (p.Value?.Value?.ToString() ?? "").Contains('/')) == true;
            bool hasAction = sp.Value.OfType<StructPropertyData>()
                .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_AIAttackAction")?.Value
                ?.OfType<ObjectPropertyData>()
                .Any(p => p.Name?.Value?.ToString() == "m_DefaultClassObject"
                       && p.Value is { Index: not 0 }) == true;
            if (!hasAnim && !hasAction) continue;
            var trans = TransitionsOf(sp);
            if (trans?.Value == null) continue;
            for (int r = 0; r < trans.Value.Length; r++)
            {
                if (trans.Value[r] is not StructPropertyData row || row.Value == null) continue;
                var fp = row.Value.OfType<FloatPropertyData>()
                    .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_fProbability");
                if (fp != null && Math.Abs(fp.Value - 33.5f) > 0.001f)
                    return (i, r, fp);
            }
        }
        return (-1, -1, null!);
    }

    private static (int tree, int row) FindCondRow(ArrayPropertyData nodes)
    {
        for (int i = 0; i < nodes.Value.Length; i++)
        {
            if (nodes.Value[i] is not StructPropertyData sp || sp.Value == null) continue;
            var trans = TransitionsOf(sp);
            if (trans?.Value == null) continue;
            for (int r = 0; r < trans.Value.Length; r++)
            {
                if (trans.Value[r] is not StructPropertyData row || row.Value == null) continue;
                if (CondIndexOf(row) > 0) return (i, r);
            }
        }
        return (-1, -1);
    }

    private static int FindEventNodeIndex(UAsset asset, ArrayPropertyData nodes)
    {
        for (int i = 0; i < nodes.Value.Length; i++)
        {
            if (nodes.Value[i] is not StructPropertyData sp || sp.Value == null) continue;
            var ev = sp.Value.OfType<ObjectPropertyData>()
                .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_Event");
            if (ev?.Value != null && ev.Value.Index != 0) return i;
        }
        return -1;
    }

    private static int FindAttackNodeIndex(ArrayPropertyData nodes)
    {
        for (int i = 0; i < nodes.Value.Length; i++)
        {
            if (nodes.Value[i] is not StructPropertyData sp || sp.Value == null) continue;
            bool hasEvent = sp.Value.OfType<ObjectPropertyData>()
                .Any(p => p.Name?.Value?.ToString() == "m_Event" && p.Value is { Index: not 0 });
            bool hasAction = sp.Value.OfType<StructPropertyData>()
                .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_AIAttackAction")?.Value
                ?.OfType<ObjectPropertyData>()
                .Any(p => p.Name?.Value?.ToString() == "m_DefaultClassObject"
                       && p.Value is { Index: not 0 }) == true;
            if (hasEvent || hasAction) continue;
            bool hasAnim = sp.Value.OfType<StructPropertyData>()
                .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_AttackInfos")?.Value
                ?.OfType<NamePropertyData>()
                .Any(p => p.Name?.Value?.ToString() == "m_Attacks"
                       && (p.Value?.Value?.ToString() ?? "").Contains('/')) == true;
            if (hasAnim) return i;
        }
        return -1;
    }

    private static readonly HashSet<string> SkippedParamProps = new(StringComparer.Ordinal)
    {
        "m_ConditionID", "m_PerConditionValueToNodeMap", "m_iSerializeVersion"
    };

    /// <summary>First (tree, row, condExport1Based, leaf) on a MATCHED node (attack or
    /// action, no redirect) whose condition export carries a mutable numeric/bool leaf
    /// outside the collector's skip set - the param walk ignores m_ConditionID etc.,
    /// so mutating those would never produce an override.</summary>
    private static (int tree, int row, int condIdx, PropertyData leaf) FindCondRowWithMutableParam(
        UAsset asset, ArrayPropertyData nodes)
    {
        for (int i = 0; i < nodes.Value.Length; i++)
        {
            if (nodes.Value[i] is not StructPropertyData sp || sp.Value == null) continue;
            if (sp.Value.OfType<IntPropertyData>()
                    .Any(p => p.Name?.Value?.ToString() == "m_NodeRedirect" && p.Value >= 0))
                continue;
            bool hasAnim = sp.Value.OfType<StructPropertyData>()
                .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_AttackInfos")?.Value
                ?.OfType<NamePropertyData>()
                .Any(p => p.Name?.Value?.ToString() == "m_Attacks"
                       && (p.Value?.Value?.ToString() ?? "").Contains('/')) == true;
            bool hasAction = sp.Value.OfType<StructPropertyData>()
                .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_AIAttackAction")?.Value
                ?.OfType<ObjectPropertyData>()
                .Any(p => p.Name?.Value?.ToString() == "m_DefaultClassObject"
                       && p.Value is { Index: not 0 }) == true;
            if (!hasAnim && !hasAction) continue;
            var trans = TransitionsOf(sp);
            if (trans?.Value == null) continue;
            for (int r = 0; r < trans.Value.Length; r++)
            {
                if (trans.Value[r] is not StructPropertyData row || row.Value == null) continue;
                int condIdx = CondIndexOf(row);
                if (condIdx <= 0 || condIdx > asset.Exports.Count) continue;
                if (asset.Exports[condIdx - 1] is not NormalExport { Data: { Count: > 0 } condExp }) continue;
                var leaf = FindMutableLeaf(condExp);
                if (leaf != null) return (i, r, condIdx, leaf);
            }
        }
        return (-1, -1, -1, null!);
    }

    private static PropertyData? FindMutableLeaf(List<PropertyData> props, int depth = 0)
    {
        if (depth > 3) return null;
        foreach (var p in props)
        {
            string name = p.Name?.Value?.ToString() ?? "";
            if (SkippedParamProps.Contains(name)) continue;
            // Safe-to-mutate scalar leaves only (Name/Enum/Byte strings risk corrupting
            // the fixture write; struct/array/map recurse).
            if (p is FloatPropertyData or IntPropertyData or Int8PropertyData
                or UInt16PropertyData or UInt32PropertyData or Int64PropertyData
                or BoolPropertyData)
                return p;
            if (p is StructPropertyData { Value: { } sv })
            {
                var hit = FindMutableLeaf(sv, depth + 1);
                if (hit != null) return hit;
            }
        }
        return null;
    }

    // ---------------------------------------------------------------- test cases

    private static void TestReaderBasics()
    {
        string file = Path.Combine(ContentRoot, "DB", "AI", "Archetypes", "Sean", "Sean_Combo_Phase1.uasset");
        var parser = NewParser();
        var nodes = parser.ReadModdedComboNodes(file, ContentRoot);
        Expect(nodes.Count > 0, "no nodes parsed");
        Expect(nodes.Any(n => n.AiKind is "Action" or "Event"), "no AI payload nodes read");
        Expect(nodes.Sum(n => n.TransitionRows.Count) > 0, "no transition rows read");
        Expect(nodes.All(n => !string.IsNullOrEmpty(n.ModdedContentRel)), "ModdedContentRel not set");
        var starts = parser.ReadModdedStartEntries(file);
        var graph = parser.LoadComboTreeFromPath(SeanP1Rel, "Staff")
            ?? throw new Exception("vanilla graph null");
        Expect(starts.Count == graph.StartEntries.Count,
            $"start entries {starts.Count} != vanilla {graph.StartEntries.Count}");
    }

    private static void TestChanceOverride()
    {
        var (source, written) = CopyToTemp(Path.Combine(ContentRoot, "DB", "AI", "Archetypes", "Sean", "Sean_Combo_Phase1.uasset"), "chance");
        var asset = Load(source);
        var (_, nodes) = ComboOf(asset);
        var (tree, row, prop) = FindNon100ProbRow(nodes);
        Expect(tree >= 0, "no probability row on a matched attack/action node");
        float original = prop.Value;
        prop.Value = 33.5f;
        asset.Write(written);

        var (result, graph) = RunImport(written, SeanP1Rel, "Staff", withStartEntries: false);
        Expect(result.ChanceOverrides >= 1, $"ChanceOverrides={result.ChanceOverrides}, expected >= 1");
        Expect(graph.TreeRowProbabilityOverrides.TryGetValue(tree, out var pMap) &&
               pMap.TryGetValue(row, out var v) && Math.Abs(v - 33.5f) < 0.001f,
            $"override missing at tree[{tree}] row {row} (original {original})");
    }

    private static void TestConditionClear()
    {
        var (source, written) = CopyToTemp(Path.Combine(ContentRoot, "DB", "AI", "Archetypes", "Sean", "Sean_Combo_Phase1.uasset"), "condclear");
        var asset = Load(source);
        var (_, nodes) = ComboOf(asset);
        var (tree, row) = FindCondRow(nodes);
        Expect(tree >= 0, "no conditioned row found");
        var rowSp = (StructPropertyData)((StructPropertyData)nodes.Value[tree]).Value!
            .OfType<StructPropertyData>().First(p => p.Name?.Value?.ToString() == "m_Transitions").Value!
            .OfType<ArrayPropertyData>().First(p => p.Name?.Value?.ToString() == "m_Transitions").Value![row];
        var cond = rowSp.Value!.OfType<ObjectPropertyData>()
            .First(p => p.Name?.Value?.ToString() == "m_ConditionInstance");
        cond.Value = new FPackageIndex(0);
        asset.Write(written);

        var (result, graph) = RunImport(written, SeanP1Rel, "Staff", withStartEntries: false);
        Expect(result.CondClears >= 1, $"CondClears={result.CondClears}, expected >= 1");
        Expect(graph.TreeRowConditionOverrides.TryGetValue(tree, out var cMap) &&
               cMap.TryGetValue(row, out var edit) && edit.ClearCondition,
            $"ClearCondition missing at tree[{tree}] row {row}");
    }

    private static void TestConditionParam()
    {
        // Sean P1 conditions are mostly internal-only exports (no editable leaves) -
        // fall back to Fajar P2 when needed.
        foreach (var (file, rel, label) in new[]
        {
            (Path.Combine(ContentRoot, "DB", "AI", "Archetypes", "Sean", "Sean_Combo_Phase1.uasset"), SeanP1Rel, "condparam"),
            (Path.Combine(ContentRoot, "DB", "AI", "Archetypes", "Fajar", "Attacks", "Fajar_Combo_P2.uasset"), FajarP2Rel, "condparam2"),
        })
        {
            var (source, written) = CopyToTemp(file, label);
            var asset = Load(source);
            var (_, nodes) = ComboOf(asset);
            var (tree, row, condIdx, leaf) = FindCondRowWithMutableParam(asset, nodes);
            if (tree < 0) continue;
            switch (leaf)
            {
                case FloatPropertyData f:
                    f.Value += 7.5f;
                    break;
                case BoolPropertyData b:
                    b.Value = !b.Value;
                    break;
                case IntPropertyData ii:
                    ii.Value += 3;
                    break;
                case Int8PropertyData i8:
                    i8.Value += 1;
                    break;
                case UInt16PropertyData u16:
                    u16.Value += 1;
                    break;
                case UInt32PropertyData u32:
                    u32.Value += 1;
                    break;
                case Int64PropertyData i64:
                    i64.Value += 1;
                    break;
                default:
                    throw new Exception($"unexpected leaf type {leaf.GetType().Name}");
            }
            asset.Write(written);

            var (result, graph) = RunImport(written, rel, "Staff", withStartEntries: false);
            Expect(result.CondParamWrites >= 1, $"CondParamWrites={result.CondParamWrites}, expected >= 1");
            Expect(graph.TreeConditionParamOverrides.ContainsKey(condIdx),
                $"TreeConditionParamOverrides missing export {condIdx}");
            return;
        }
        throw new Exception("no conditioned row with a mutable param leaf in Sean P1 or Fajar P2");
    }

    private static void TestStartEntryProbability()
    {
        var (source, written) = CopyToTemp(Path.Combine(ContentRoot, "DB", "AI", "Archetypes", "Fajar", "Attacks", "Fajar_Combo_P2.uasset"), "start");
        var asset = Load(source);
        var cont = StartContainerOf(asset);
        Expect(cont?.Value?.Length > 0, "no start entries in fixture");
        var entry = (StructPropertyData)cont!.Value![0];
        var rows = entry.Value!.OfType<StructPropertyData>()
            .First(p => p.Name?.Value?.ToString() == "m_ComboTransitions").Value!
            .OfType<ArrayPropertyData>().First(p => p.Name?.Value?.ToString() == "m_Transitions").Value;
        Expect(rows!.Length > 0, "start entry has no transitions");
        var row0 = (StructPropertyData)rows[0];
        var fp = row0.Value!.OfType<FloatPropertyData>()
            .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_fProbability");
        Expect(fp != null, "no m_fProbability on start transition");
        fp!.Value = 12.5f;
        asset.Write(written);

        var (result, graph) = RunImport(written, FajarP2Rel, "Staff");
        Expect(result.StartTransitionsChanged >= 1,
            $"StartTransitionsChanged={result.StartTransitionsChanged}, expected >= 1");
        Expect(result.StartEntriesChanged >= 1, $"StartEntriesChanged={result.StartEntriesChanged}, expected >= 1");
        var changed = graph.StartEntries
            .SelectMany(e => e.Transitions.Select(t => (entry: e, t)))
            .Where(x => Math.Abs(x.t.Probability - 12.5f) < 0.001f)
            .ToList();
        Expect(changed.Count > 0, "no start transition shows Probability=12.5");
        Expect(changed.All(x => Math.Abs(x.t.OriginalProbability - 12.5f) > 0.001f),
            "OriginalProbability was overwritten");
    }

    private static void TestAiPayloadEventToAttack()
    {
        // Fajar P2 carries event nodes (Sean P1 is action-only).
        var (source, written) = CopyToTemp(Path.Combine(ContentRoot, "DB", "AI", "Archetypes", "Fajar", "Attacks", "Fajar_Combo_P2.uasset"), "aipayload");
        var asset = Load(source);
        var (_, nodes) = ComboOf(asset);
        int evIdx = FindEventNodeIndex(asset, nodes);
        int atkIdx = FindAttackNodeIndex(nodes);
        Expect(evIdx >= 0 && atkIdx >= 0 && evIdx != atkIdx, $"event/attack node indices bad ({evIdx}/{atkIdx})");

        var evNode = (StructPropertyData)nodes.Value[evIdx];
        var evProp = evNode.Value!.OfType<ObjectPropertyData>()
            .First(p => p.Name?.Value?.ToString() == "m_Event");
        var atkNode = (StructPropertyData)nodes.Value[atkIdx];
        // Every node carries a null m_Event property - SET it, never append a second
        // one (the reader takes the first match).
        var atkEv = atkNode.Value!.OfType<ObjectPropertyData>()
            .FirstOrDefault(p => p.Name?.Value?.ToString() == "m_Event");
        if (atkEv != null)
            atkEv.Value = evProp.Value;
        else
            atkNode.Value.Add(new ObjectPropertyData
            {
                Name = FName.FromString(asset, "m_Event"),
                Value = evProp.Value
            });
        asset.Write(written);

        var (result, graph) = RunImport(written, FajarP2Rel, "Staff", withStartEntries: false);
        Expect(result.AiChanges >= 1, $"AiChanges={result.AiChanges}, expected >= 1");
        Expect(graph.Nodes.Any(n => n.TreeIndex == atkIdx && n.IsEventNode && !string.IsNullOrEmpty(n.EventName)),
            $"vanilla tree[{atkIdx}] not marked as event node");
    }

    private static void TestSurfacedEventNode()
    {
        // Fajar P2 carries event nodes (Sean P1 is action-only).
        var (source, written) = CopyToTemp(Path.Combine(ContentRoot, "DB", "AI", "Archetypes", "Fajar", "Attacks", "Fajar_Combo_P2.uasset"), "surface");
        var asset = Load(source);
        var (_, nodes) = ComboOf(asset);
        int evIdx = FindEventNodeIndex(asset, nodes);
        Expect(evIdx >= 0, "no event node with m_Event export found");
        int appendedIdx = nodes.Value.Length;

        var clone = (StructPropertyData)CloneProp(nodes.Value[evIdx]);
        var nameIdx = clone.Value!.FindIndex(p => p.Name?.Value?.ToString() == "m_Name");
        Expect(nameIdx >= 0, "cloned node has no m_Name");
        clone.Value[nameIdx] = new NamePropertyData
        {
            Name = FName.FromString(asset, "m_Name"),
            Value = FName.FromString(asset, "SmokeExtraAiNode")
        };
        var arr = new PropertyData[nodes.Value.Length + 1];
        Array.Copy(nodes.Value, arr, nodes.Value.Length);
        arr[^1] = clone;
        nodes.Value = arr;
        asset.Write(written);

        var (result, graph) = RunImport(written, FajarP2Rel, "Staff", withStartEntries: false);
        Expect(result.Surfaced >= 1, $"Surfaced={result.Surfaced}, expected >= 1");
        var surfaced = graph.Nodes.FirstOrDefault(n =>
            n.Kind == ComboNodeKind.ImportedNew && n.Name == "SmokeExtraAiNode");
        Expect(surfaced != null, "SmokeExtraAiNode not surfaced");
        Expect(surfaced!.IsEventNode && !string.IsNullOrEmpty(surfaced.EventName),
            "surfaced node has no event payload");
        Expect(!string.IsNullOrEmpty(surfaced.ImportComboRel),
            "surfaced node has no ImportComboRel");
        Expect(surfaced.ImportSourceTreeIndex == appendedIdx,
            $"ImportSourceTreeIndex={surfaced.ImportSourceTreeIndex}, expected {appendedIdx}");
    }

    // ---------------------------------------------------------------- real paks

    /// <summary>
    /// Extracts a real mod pak (UnrealPak, same flow as MainWindow import), discovers its
    /// combo trees through BuildImportComboCatalog, runs the vanilla-compare pipeline and
    /// asserts the Bug 1-3 regressions: no opaque Node_{idx} display names on surfaced
    /// nodes (purple-duplicate fix), WW Kuroki_P1 edge fan collapsed, EH Kuroki_P1
    /// surfaced count down from 112, and every EH node anim present in vanilla content
    /// (so the import sweep can no longer create Custom cards for them). Prints a
    /// per-unit table of surfaced/edge/missing-anim numbers for layout review.
    /// </summary>
    private static void RunRealPak(string label, string pakPath)
    {
        var only = Environment.GetEnvironmentVariable("SIFU_SMOKE_PAKS");
        if (!string.IsNullOrEmpty(only) &&
            !only.Split(',').Any(s => s.Trim().Equals(label, StringComparison.OrdinalIgnoreCase)))
            return;
        if (!File.Exists(pakPath))
        {
            Console.WriteLine($"    (pak not found - skipped: {pakPath})");
            return;
        }

        var stagingRoot = Path.Combine(Path.GetTempPath(), "SifuImpSmoke" + label);
        var extractDir = Path.Combine(stagingRoot, "a", "b", "c");
        if (Directory.Exists(stagingRoot)) Directory.Delete(stagingRoot, true);
        Directory.CreateDirectory(extractDir);
        try
        {
            var pakCopy = Path.Combine(stagingRoot, "original.pak");
            File.Copy(pakPath, pakCopy, true);

            var unrealPak = ContentExtractor.FindUnrealPak()
                ?? throw new Exception("UnrealPak not found (tools\\ue4\\UnrealPak\\UnrealPak.exe)");
            var keys = ContentExtractor.EnsurePakKeysFile();
            var psi = new ProcessStartInfo
            {
                FileName = unrealPak,
                Arguments = keys != null && File.Exists(keys)
                    ? $"\"{pakCopy}\" -CryptoKeys=\"{keys}\" -Extract \"{extractDir}\""
                    : $"\"{pakCopy}\" -Extract \"{extractDir}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = ContentExtractor.EnsureUnrealPakWorkingDirectory(),
            };
            using (var proc = Process.Start(psi) ?? throw new Exception("Failed to start UnrealPak"))
            {
                string stdout = proc.StandardOutput.ReadToEnd();
                string stderr = proc.StandardError.ReadToEnd();
                if (!proc.WaitForExit(300000))
                {
                    try { proc.Kill(true); } catch { }
                    throw new Exception("UnrealPak timed out after 300 s");
                }
                if (proc.ExitCode != 0)
                    throw new Exception($"UnrealPak exit {proc.ExitCode}: {(stderr + "\n" + stdout).Trim()}");
            }

            var catalog = MainWindow.BuildImportComboCatalog();
            var candidates = Directory.GetFiles(extractDir, "*.uasset", SearchOption.AllDirectories)
                .Where(f => catalog.ContainsKey(Path.GetFileNameWithoutExtension(f)))
                .Select(f => (path: f, rel: Path.GetRelativePath(extractDir, f),
                              entry: catalog[Path.GetFileNameWithoutExtension(f)]))
                .ToList();

            static bool IsBackupVariant(string rel) =>
                rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Any(seg => seg.Equals("0Backup", StringComparison.OrdinalIgnoreCase)
                             || seg.Equals("Old", StringComparison.OrdinalIgnoreCase)
                             || seg.Contains("_Copy", StringComparison.OrdinalIgnoreCase));
            var chosen = new Dictionary<string, (string path, MainWindow.ImportComboEntry entry)>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var c in candidates) if (!IsBackupVariant(c.rel)) chosen.TryAdd(c.entry.UnitKey, (c.path, c.entry));
            foreach (var c in candidates) if (IsBackupVariant(c.rel)) chosen.TryAdd(c.entry.UnitKey, (c.path, c.entry));
            if (chosen.Count == 0) throw new Exception($"no combo trees discovered in {Path.GetFileName(pakPath)}");

            string? overlay = MainWindow.FindContentRootUnder(extractDir);
            var parser = NewParser();
            if (overlay != null) parser.SetOverlayProvider(parser.CreateOverlayProvider(overlay));

            Console.WriteLine($"    [{label}] {Path.GetFileName(pakPath)}: {chosen.Count} unit tree(s), overlay={(overlay != null ? "yes" : "no")}");
            int missingTotal = 0;
            var failures = new List<string>();
            void Check(bool ok, string msg)
            {
                if (!ok) failures.Add(msg);
            }
            foreach (var (unitKey, (treePath, entry)) in chosen)
            {
                var modded = parser.ReadModdedComboNodes(treePath, overlay);
                if (modded.Count == 0)
                {
                    Console.WriteLine($"    {label} {unitKey}: no modded nodes parsed - skipped");
                    continue;
                }
                var starts = parser.ReadModdedStartEntries(treePath);
                var graph = parser.LoadComboTreeFromPath(entry.GamePath, entry.WeaponName);
                if (graph == null)
                {
                    Console.WriteLine($"    {label} {unitKey}: vanilla graph null ({entry.GamePath}) - skipped");
                    continue;
                }

                int edgesBefore = graph.Edges.Count;
                var result = parser.ApplyModdedComboToVanilla(
                    graph, modded, entry.WeaponName, starts.Count > 0 ? starts : null);
                int edgesDelta = graph.Edges.Count - edgesBefore;

                var surfacedNodes = graph.Nodes.Where(n => n.Kind == ComboNodeKind.ImportedNew).ToList();
                int missing = graph.Nodes.Count(n =>
                    !n.IsRoot && !string.IsNullOrEmpty(n.AnimPath)
                    && n.AnimPath.Contains("/Animations/", StringComparison.OrdinalIgnoreCase)
                    && !n.AnimPath.Contains("/DB/", StringComparison.OrdinalIgnoreCase)
                    && !AnimOnVanillaDisk(ContentRoot, n.AnimPath));
                missingTotal += missing;

                Console.WriteLine(
                    $"    {label} {unitKey}: mod={modded.Count} surfaced={result.Surfaced} " +
                    $"extraNodes={surfacedNodes.Count} edgesDelta={edgesDelta} " +
                    $"graph={graph.Nodes.Count}/{graph.Edges.Count} missingOnDisk={missing}");
                var extraIdx = string.Join(",", surfacedNodes.Select(n => n.TreeIndex).OrderBy(x => x));
                var conduitKeys = graph.ConduitTargets == null
                    ? ""
                    : string.Join(",", graph.ConduitTargets.Keys.OrderBy(k => k));
                int redirs = graph.Nodes.Count(n => n.IsRedirect && n.TreeIndex >= 0);
                Console.WriteLine(
                    $"    {label} {unitKey}: extraTreeIdx=[{extraIdx}] redirs={redirs} conduits=[{conduitKeys}]");

                foreach (var n in surfacedNodes)
                {
                    var disp = string.IsNullOrEmpty(n.DisplayName) ? n.Name : n.DisplayName;
                    Check(!string.IsNullOrWhiteSpace(disp),
                        $"{label} {unitKey}: surfaced node tree[{n.TreeIndex}] has no display name " +
                        $"(name='{n.Name}' db='{n.DefaultDBPath}' anim='{n.AnimPath}' " +
                        $"redir={n.IsRedirect}->{n.RedirectTargetTreeIndex} action='{n.ActionClass}' event='{n.EventName}')");
                    Check(!Regex.IsMatch(disp?.Trim() ?? "", @"^Node_\d+$"),
                        $"{label} {unitKey}: surfaced node named '{disp}' (tree[{n.TreeIndex}]) " +
                        $"name='{n.Name}' db='{n.DefaultDBPath}' anim='{n.AnimPath}' " +
                        $"redir={n.IsRedirect}->{n.RedirectTargetTreeIndex} action='{n.ActionClass}' event='{n.EventName}' " +
                        $"dirs={n.DirectionLabel} kind={n.Kind}");
                }

                if (label == "EH" && unitKey == "Kuroki|Kuroki_P1")
                {
                    Check(result.Surfaced >= 40 && result.Surfaced <= 55,
                        $"EH Kuroki_P1 surfaced={result.Surfaced}, expected 40..55 (pre-fix 112)");
                }
                if (label == "WW" && unitKey == "Kuroki|Kuroki_P1")
                {
                    Check(result.Surfaced <= 60,
                        $"WW Kuroki_P1 surfaced={result.Surfaced}, expected <= 60 (pre-fix 118)");
                    Check(edgesDelta < 2000,
                        $"WW Kuroki_P1 edgesDelta={edgesDelta}, expected < 2000 (pre-fix 3964)");
                }
            }
            parser.SetOverlayProvider(null);

            if (label == "EH")
            {
                Check(missingTotal == 0,
                    $"EH: {missingTotal} node anim(s) absent from vanilla content - would remain Custom cards");
            }
            else
            {
                Console.WriteLine(
                    $"    [{label}] {missingTotal} node anim(s) absent from vanilla content (kept as Custom cards)");
            }

            if (failures.Count > 0)
                throw new Exception(string.Join("\n         ", failures));
        }
        finally
        {
            try { if (Directory.Exists(stagingRoot)) Directory.Delete(stagingRoot, true); } catch { }
        }
    }

    private static bool AnimOnVanillaDisk(string contentDir, string animPath)
    {
        var rel = VoiceArchetypeFixer.NormalizeRel(animPath);
        if (string.IsNullOrEmpty(rel)) return false;
        return File.Exists(Path.Combine(contentDir,
            rel.Replace('/', Path.DirectorySeparatorChar) + ".uasset"));
    }
}
