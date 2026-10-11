using System.Collections;
using System.Reflection;
using System.Text;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Objects.Properties;
using CUE4Parse.UE4.Versions;

internal static class Program
{
    private static readonly string[] Targets =
    {
        "Game/DB/AI/Archetypes/FireDisciple/Variations/Combo/FireDisciple_Base_Combo",
        "Game/DB/AI/Archetypes/Sean/Sean_Combo_Phase2",
        "Game/DB/_MainChar/Combos/MainChar_ComboTree",
        "Game/DB/AI/Archetypes/Yang/_DB/Phase1/Yang_P1_Combo",
        "Game/DB/AI/Archetypes/Kuroki/Kuroki_ComboPhase1_NEW",
        "Game/DB/AI/Archetypes/Fajar/Attacks/Fajar_Combo_P2",
        "Game/DB/AI/Archetypes/Grunt/_Base/Grunt_Base_Combo",
    };

    private static readonly string DefaultRoot =
        @"C:\Users\Charles\Downloads\Sifu Modding\Movesets Prototypes\Moveset Maker\SifuMovesetEditor\bin\Debug\net10.0-windows\GameContent";

    private static readonly HashSet<string> PrimitiveNames = new()
    {
        "Boolean", "Byte", "SByte", "Int16", "UInt16", "Int32", "UInt32",
        "Int64", "UInt64", "Single", "Double", "String", "Char", "Guid", "Void"
    };

    private static int Main(string[] args)
    {
        bool rowsMode = args.Any(a => a == "--rows");
        bool classesMode = args.Any(a => a == "--classes");
        var positional = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();
        string? rootArg = positional.FirstOrDefault(a => !a.Contains('/'));
        string[] paths = positional.Where(a => a.Contains('/')).ToArray();

        var root = rootArg ?? DefaultRoot;

        var provider = new DefaultFileProvider(root, SearchOption.AllDirectories,
            new VersionContainer(EGame.GAME_UE4_26), StringComparer.OrdinalIgnoreCase);
        provider.Initialize();

        if (rowsMode)
        {
            foreach (var path in paths.Length > 0 ? paths : Targets)
            {
                try { RowsFor(provider, path); }
                catch (Exception ex) { Console.WriteLine($"  FAIL: {ex.GetType().Name}: {ex.Message}"); }
            }
            return 0;
        }

        if (classesMode)
        {
            ClassesScan(provider);
            return 0;
        }

        ShapeReport();

        foreach (var path in Targets)
        {
            Console.WriteLine($"===== {path}");
            try
            {
                DumpPackage(provider, path);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  FAIL: {ex.GetType().Name}: {ex.Message}");
            }
        }
        return 0;
    }

    // ------------------------------------------------------------- forensics modes

    /// <summary>Per-tree condition-row report: every condition row (node/row/prob/keys/
    /// targets), instance sharing across rows, key-shape stats, and a full dump of each
    /// unique condition export's m_PerConditionValueToNodeMap.</summary>
    private static void RowsFor(DefaultFileProvider provider, string path)
    {
        Console.WriteLine($"===== {path}");
        var obj = provider.SafeLoadPackageObject<UObject>(path);
        if (obj == null) { Console.WriteLine("  null object"); return; }

        var rows = new List<(int node, int row, int expIdx, string cls, float prob,
            string keys, string targets, object? exp, object rowObj)>();
        int total = WalkComboRows(obj, (n, r, row) =>
        {
            var (idx, exp, cls) = CondRef(row);
            rows.Add((n, r, idx, cls, ProbOf(row), KeySetOf(row),
                idx > 0 ? (DescribeTargets(FindTag(row, "m_TargetNodes")) ?? "") : "",
                exp, row));
        });
        int condRows = rows.Count(x => x.expIdx > 0);
        Console.WriteLine($"  rows={total} condRows={condRows}");

        foreach (var g in rows.GroupBy(x => x.keys).OrderByDescending(g => g.Count()))
            Console.WriteLine($"  KEYSETS {g.Key}: x{g.Count()}");

        foreach (var r in rows.Where(x => x.expIdx > 0))
            Console.WriteLine($"    node[{r.node}] row[{r.row}] p={r.prob:0.##} keys={r.keys} -> [{r.targets}] cond=exp{r.expIdx} {r.cls}");

        foreach (var g in rows.Where(x => x.expIdx > 0).GroupBy(x => x.expIdx))
        {
            var list = g.ToList();
            if (list.Count > 1)
            {
                float sum = list.Sum(x => x.prob);
                Console.WriteLine($"  SHARED exp{g.Key} {list[0].cls}: "
                    + string.Join(", ", list.Select(x => $"node[{x.node}].row[{x.row}](p={x.prob:0.##})"))
                    + $"  sum={sum:0.##}");
            }
            var exp = list[0].exp;
            Console.WriteLine($"  INST exp{g.Key} {list[0].cls} props=[{PropNamesOf(exp)}]");
            DumpPerCond(exp, list[0].rowObj, "    ");
        }
    }

    /// <summary>Inventory: condition class -> files using it (across every combo file
    /// under Content/DB the provider can see). Seed for the add-condition catalog.</summary>
    private static void ClassesScan(DefaultFileProvider provider)
    {
        var files = provider.Files.Keys
            .Where(k => k.Contains("/Content/DB/", StringComparison.OrdinalIgnoreCase)
                && k.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)
                && k.Contains("Combo", StringComparison.OrdinalIgnoreCase))
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .ToList();
        Console.WriteLine($"===== CLASSES: scanning {files.Count} combo files");

        var inv = new SortedDictionary<string, (HashSet<string> files, int instances, int perCond)>(StringComparer.Ordinal);
        int unresolved = 0, scanned = 0;
        foreach (var f in files)
        {
            try
            {
                string? gamePath = ToGamePath(f);
                if (gamePath == null) continue;
                var obj = provider.SafeLoadPackageObject<UObject>(gamePath);
                if (obj == null) continue;
                scanned++;
                WalkComboRows(obj, (_, _, row) =>
                {
                    var (idx, exp, cls) = CondRef(row);
                    if (idx <= 0) return;
                    if (cls == "?") unresolved++;
                    if (!inv.TryGetValue(cls, out var e)) inv[cls] = e = (new HashSet<string>(StringComparer.OrdinalIgnoreCase), 0, 0);
                    e.files.Add(f);
                    e.instances++;
                    if (exp != null && FindNamedTag(exp, "m_PerConditionValueToNodeMap").where != null)
                        inv[cls] = (e.files, e.instances, e.perCond + 1);
                });
            }
            catch { }
        }
        Console.WriteLine($"  scanned={scanned} unresolvedClassRefs={unresolved}");
        foreach (var kv in inv)
            Console.WriteLine($"  {kv.Key}: {kv.Value.instances} instances in {kv.Value.files.Count} files, perCondMap={kv.Value.perCond}, e.g. {kv.Value.files.First()}");
    }

    private static string? ToGamePath(string key)
    {
        int i = key.IndexOf("/Content/", StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;
        string rel = key[(i + "/Content/".Length)..];
        if (rel.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase))
            rel = rel[..^".uasset".Length];
        return "Game/" + rel;
    }

    private static int WalkComboRows(UObject obj, Action<int, int, object> onRow)
    {
        var nodesTag = FindTag(obj, "m_Nodes");
        if (nodesTag == null) return 0;
        int count = 0, nodeIdx = -1;
        foreach (var elem in EnumOf(ValueOf(nodesTag), "Properties"))
        {
            nodeIdx++;
            var fs = ValueOf(elem);
            var node = fs == null ? null : GetP(fs, "StructType");
            if (node == null) continue;
            var trans = FindTag(node, "m_Transitions");
            if (trans is not StructProperty) continue;
            var transFs = ValueOf(trans);
            var transFallback = transFs == null ? null : GetP(transFs, "StructType");
            if (transFallback == null) continue;
            var transArr = FindTag(transFallback, "m_Transitions");
            if (transArr == null) continue;
            int rowIdx = -1;
            foreach (var rowElem in EnumOf(ValueOf(transArr), "Properties"))
            {
                rowIdx++;
                count++;
                var rowFs = ValueOf(rowElem);
                var row = rowFs == null ? null : GetP(rowFs, "StructType");
                if (row == null) continue;
                onRow(nodeIdx, rowIdx, row);
            }
        }
        return count;
    }

    private static (int idx, object? exp, string cls) CondRef(object row)
    {
        var cond = FindTag(row, "m_ConditionInstance");
        if (cond == null) return (0, null, "");
        var fpi = ValueOf(cond);
        if (fpi == null) return (0, null, "");
        int idx = GetP(fpi, "Index") is int i ? i : 0;
        if (idx <= 0) return (0, null, "");
        var ro = GetP(fpi, "ResolvedObject");
        object? exp = null;
        try
        {
            var raw = ro == null ? null : GetP(ro, "Object");
            if (raw != null)
                exp = raw.GetType().Name.StartsWith("Lazy", StringComparison.Ordinal)
                    ? GetP(raw, "Value")
                    : raw;
        }
        catch { exp = null; }
        string cls = "?";
        var c = ro == null ? null : GetP(ro, "Class");
        if (c != null)
        {
            var cn = GetP(c, "Name");
            cls = GetP(cn, "Text")?.ToString() ?? cn?.ToString() ?? "?";
        }
        return (idx, exp, cls);
    }

    private static float ProbOf(object row)
    {
        var raw = ValueOf(FindTag(row, "m_fProbability"));
        return raw switch { float f => f, double d => (float)d, _ => -1f };
    }

    private static string KeySetOf(object row)
    {
        var t = FindTag(row, "m_TargetNodes");
        if (t == null) return "<none>";
        var keys = new List<int>();
        foreach (var kv in EnumOf(ValueOf(t), "Properties"))
        {
            var k = GetP(kv, "Key");
            var simple = SimpleVal(k);
            keys.Add(simple != null && int.TryParse(simple, out var n) ? n : -1);
        }
        keys.Sort();
        return "{" + string.Join(",", keys) + "}";
    }

    private static string PropNamesOf(object? exp)
    {
        if (exp == null) return "unresolved";
        var names = new List<string>();
        foreach (var p in EnumOf(exp, "Properties"))
        {
            var nm = GetP(p, "Name");
            names.Add(GetP(nm, "Text")?.ToString() ?? nm?.ToString() ?? "?");
        }
        return string.Join(", ", names);
    }

    private static void DumpPerCond(object? exp, object row, string ind)
    {
        var (where, tag, size) = FindNamedTag(exp, "m_PerConditionValueToNodeMap");
        if (tag == null && where == null)
            (where, tag, size) = FindNamedTag(row, "m_PerConditionValueToNodeMap");
        if (where == null) { Console.WriteLine($"{ind}PerCondValueToNodeMap: <absent>"); return; }
        if (tag == null) { Console.WriteLine($"{ind}PerCondValueToNodeMap[{where}]: present but Tag=null (Size={size})"); return; }
        var map = ValueOf(tag);
        int n = 0;
        foreach (var kv in EnumOf(map, "Properties"))
        {
            n++;
            var k = GetP(kv, "Key");
            var v = GetP(kv, "Value");
            Console.WriteLine($"{ind}PerCondValueToNodeMap[{where}] key={SimpleVal(k) ?? k?.GetType().Name} -> {v?.GetType().Name}");
            DumpStructish(v, ind + "  ");
        }
        if (n == 0) Console.WriteLine($"{ind}PerCondValueToNodeMap[{where}]: empty (Size={size})");
    }

    private static (string? where, object? tag, int size) FindNamedTag(object? holder, string name)
    {
        if (holder == null) return (null, null, -1);
        foreach (var p in EnumOf(holder, "Properties"))
        {
            var nm = GetP(p, "Name");
            var txt = GetP(nm, "Text")?.ToString() ?? nm?.ToString();
            if (txt != name) continue;
            return ("found", GetP(p, "Tag"), GetP(p, "Size") is int s ? s : -1);
        }
        return (null, null, -1);
    }

    private static void DumpStructish(object? v, string ind)
    {
        if (v == null) { Console.WriteLine($"{ind}null"); return; }
        string tn = v.GetType().Name;
        if (tn.EndsWith("Property", StringComparison.Ordinal)
            && v.GetType().Namespace != null
            && v.GetType().Namespace.Contains("Properties", StringComparison.Ordinal))
            v = ValueOf(v) ?? v;
        if (GetP(v, "Properties") is not IEnumerable en)
        {
            Console.WriteLine($"{ind}{v.GetType().Name} = {DescribeVal(v)}");
            return;
        }
        foreach (var p in en)
        {
            if (p == null) continue;
            var nm = GetP(p, "Name");
            string name = nm == null ? null : (GetP(nm, "Text")?.ToString() ?? nm.ToString());
            object? val;
            if (name != null)
            {
                val = ValueOf(GetP(p, "Tag")) ?? p;
            }
            else
            {
                name = GetP(p, "Key") != null ? "entry" : p.GetType().Name;
                val = GetP(p, "Value") ?? p;
            }
            if (val != null && val.GetType().Name == "UScriptMap")
            {
                int c = 0;
                foreach (var ikv in EnumOf(val, "Properties"))
                {
                    c++;
                    Console.WriteLine($"{ind}{name}[{c}]: {DescribeVal(GetP(ikv, "Key"))} -> {DescribeVal(GetP(ikv, "Value"))}");
                }
                if (c == 0) Console.WriteLine($"{ind}{name}: empty");
            }
            else
            {
                Console.WriteLine($"{ind}{name} = {DescribeVal(val)}");
            }
        }
    }

    private static string DescribeVal(object? v)
    {
        if (v == null) return "null";
        string tn = v.GetType().Name;
        if (tn == "FName") return $"FName(\"{GetP(v, "Text")}\")";
        if (tn == "FPackageIndex")
        {
            int idx = GetP(v, "Index") is int i ? i : 0;
            return idx > 0 ? $"export:{idx}" : idx < 0 ? $"import:{-idx}" : "null(0)";
        }
        if (PrimitiveNames.Contains(tn)) return $"{v} ({tn})";
        if (tn.EndsWith("Property", StringComparison.Ordinal)
            && v.GetType().Namespace != null
            && v.GetType().Namespace.Contains("Properties", StringComparison.Ordinal))
            return DescribeVal(ValueOf(v));
        if (tn == "UScriptMap") return $"map[{EnumOf(v, "Properties").Count()}]";
        if (tn == "UScriptArray") return $"arr[{EnumOf(v, "Properties").Count()}]";
        var simple = SimpleVal(v);
        if (simple != null) return simple;
        return tn;
    }

    private static void ShapeReport()
    {
        Console.WriteLine("===== TYPE SHAPES");
        Type[] types;
        try { types = typeof(UObject).Assembly.GetTypes(); }
        catch (System.Reflection.ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray()!; }

        foreach (var t in types.Where(t => t.Namespace == "CUE4Parse.UE4.Assets.Objects.Properties").OrderBy(t => t.Name))
            Console.WriteLine($"  ptype {t.Name} : {t.BaseType?.Name}");

        foreach (var name in new[]
                 {
                     "CUE4Parse.UE4.Assets.Objects.UScriptMap",
                     "CUE4Parse.UE4.Assets.Objects.UScriptArray",
                     "CUE4Parse.UE4.Assets.Objects.FPropertyTag",
                     "CUE4Parse.UE4.Assets.Objects.FScriptStruct",
                     "CUE4Parse.UE4.Assets.Objects.FStructFallback",
                     "CUE4Parse.UE4.Objects.GameplayTags.FGameplayTag",
                     "CUE4Parse.UE4.Objects.GameplayTags.FGameplayTagContainer",
                     "CUE4Parse.UE4.Objects.UObject.FName",
                     "CUE4Parse.UE4.Assets.ResolvedObject"
                 })
        {
            var t = types.FirstOrDefault(x => x.FullName == name);
            if (t == null) { Console.WriteLine($"  ?? {name}"); continue; }
            Console.WriteLine($"  == {t.FullName} : {t.BaseType?.Name}");
            foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Instance))
                Console.WriteLine($"     field {f.FieldType.Name} {f.Name}");
            foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                Console.WriteLine($"     prop {p.PropertyType.Name} {p.Name}");
        }

        var pkg = types.FirstOrDefault(x => x.FullName == "CUE4Parse.UE4.Assets.Package");
        if (pkg != null)
        {
            foreach (var n in pkg.GetNestedTypes(BindingFlags.Public))
            {
                Console.WriteLine($"     == nested {n.FullName} : {n.BaseType?.Name}");
                foreach (var f in n.GetFields(BindingFlags.Public | BindingFlags.Instance))
                    Console.WriteLine($"       field {f.FieldType.Name} {f.Name}");
                foreach (var p in n.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                    Console.WriteLine($"       prop {p.PropertyType.Name} {p.Name}");
            }
        }
    }

    private static void DumpPackage(DefaultFileProvider provider, string gamePath)
    {
        var obj = provider.SafeLoadPackageObject<UObject>(gamePath);
        if (obj == null)
        {
            Console.WriteLine("  null object");
            return;
        }

        var props = GetP(obj, "Properties");
        Console.WriteLine($"  obj type={obj.GetType().FullName} props={(props == null ? "null" : "seq")}");
        if (props is IEnumerable pen)
        {
            int pc = 0;
            foreach (var p in pen)
            {
                pc++;
                if (pc <= 12 && p != null)
                {
                    var nm = GetP(p, "Name");
                    Console.WriteLine($"    top[{pc - 1}] name={nm} type={GetP(p, "Tag")?.GetType().Name}");
                }
            }
            Console.WriteLine($"  top-level property count: {pc}");
        }

        var nodesTag = FindTag(obj, "m_Nodes");
        if (nodesTag == null)
        {
            Console.WriteLine("  no m_Nodes");
            return;
        }

        var nodeFallbacks = new List<object>();
        foreach (var elem in EnumOf(ValueOf(nodesTag), "Properties"))
        {
            var fs = ValueOf(elem);
            var st = fs == null ? null : GetP(fs, "StructType");
            if (st != null) nodeFallbacks.Add(st);
        }
        Console.WriteLine($"  nodes: {nodeFallbacks.Count}");

        for (int i = 0; i < nodeFallbacks.Count; i++)
        {
            var node = nodeFallbacks[i];
            var tags = TagsOf(node);

            var inv = new StringBuilder();
            foreach (var (name, tag) in tags)
            {
                inv.Append(name).Append('(').Append(tag?.GetType().Name ?? "null").Append(") ");
            }
            string nodeName = PropText(FindTag(node, "m_Name")) ?? "";
            if (i == 0)
                Console.WriteLine($"  node[{i}] '{nodeName}': {inv.ToString().TrimEnd()}");

            var reqTags = FindTag(node, "m_RequiredTags");
            if (reqTags != null)
            {
                Console.WriteLine($"    -- node[{i}] '{nodeName}' m_RequiredTags:");
                Dump(reqTags, "tag", 3, 8);
            }

            var condAttacks = FindTag(node, "m_ConditionalAttacks");
            if (condAttacks != null)
            {
                var caDump = DumpToString(condAttacks, "tag", 0, 10);
                if (!caDump.Contains("(unhandled)") && caDump.Length > 60)
                {
                    Console.WriteLine($"    -- node[{i}] '{nodeName}' m_ConditionalAttacks:");
                    Console.WriteLine(caDump);
                }
                else
                {
                    Console.WriteLine($"    node[{i}] '{nodeName}' m_ConditionalAttacks: <empty-or-unhandled {caDump.Length} chars>");
                }
            }

            var trans = FindTag(node, "m_Transitions");
            if (trans == null) continue;
            if (!(trans is StructProperty)) continue;
            var transFs = ValueOf(trans);
            var transFallback = transFs == null ? null : GetP(transFs, "StructType");
            if (transFallback == null) continue;
            var transArrTag = FindTag(transFallback, "m_Transitions");
            if (transArrTag == null) continue;
            int rowIdx = -1;
            int dumpedConds = 0;
            foreach (var rowElem in EnumOf(ValueOf(transArrTag), "Properties"))
            {
                rowIdx++;
                var rowFs = ValueOf(rowElem);
                var row = rowFs == null ? null : GetP(rowFs, "StructType");
                if (row == null) continue;
                var cond = FindTag(row, "m_ConditionInstance");
                if (cond == null) continue;
                var input = PropText(FindTag(row, "m_eInputTransition")) ?? "?";
                var targets = DescribeTargets(FindTag(row, "m_TargetNodes"));
                if (dumpedConds < 5)
                {
                    dumpedConds++;
                    Console.WriteLine($"    -- node[{i}] row[{rowIdx}] input={input} targets={targets} cond:");
                    Dump(cond, "cond", 3, 16);
                }
            }
        }
    }

    private static string? DescribeTargets(object? targetNodesTag)
    {
        if (targetNodesTag == null) return "<none>";
        var sb = new StringBuilder();
        foreach (var kv in EnumOf(ValueOf(targetNodesTag), "Properties"))
        {
            var k = GetP(kv, "Key");
            var v = GetP(kv, "Value");
            string ks = k == null ? "?" : (PropText(k) ?? k.GetType().Name + "(" + SimpleVal(k) + ")");
            string vs = v == null ? "?" : (SimpleVal(v) ?? v.GetType().Name);
            if (sb.Length > 0) sb.Append(", ");
            sb.Append(ks).Append("->").Append(vs);
        }
        return sb.ToString();
    }

    private static string? SimpleVal(object o)
    {
        var t = o.GetType();
        if (PrimitiveNames.Contains(t.Name)) return o.ToString();
        if (t.Name == "FName") return GetP(o, "Text")?.ToString();
        var val = GetP(o, "Value");
        if (val != null && val != o) return SimpleVal(val);
        return null;
    }

    private static string? PropText(object? tag)
    {
        if (tag == null) return null;
        var v = ValueOf(tag);
        if (v == null) return null;
        if (v.GetType().Name == "FName") return GetP(v, "Text")?.ToString();
        return SimpleVal(v);
    }

    private static object? FindTag(object holder, string name)
    {
        foreach (var p in TagsOf(holder))
            if (p.Item1 == name) return p.Item2;
        return null;
    }

    private static IEnumerable<(string, object?)> TagsOf(object holder)
    {
        var props = GetP(holder, "Properties");
        if (props is not IEnumerable en) yield break;
        foreach (var item in en)
        {
            if (item == null) continue;
            var name = GetP(item, "Name");
            string? text = name != null ? (GetP(name, "Text")?.ToString() ?? name.ToString()) : null;
            yield return (text ?? "?", GetP(item, "Tag"));
        }
    }

    private static object? ValueOf(object? tagType)
    {
        if (tagType == null) return null;
        return GetP(tagType, "Value");
    }

    private static IEnumerable<object?> EnumOf(object? o, string prop)
    {
        if (GetP(o, prop) is not IEnumerable en) yield break;
        foreach (var item in en) yield return item;
    }

    private static object? GetP(object o, string name)
    {
        try
        {
            const BindingFlags fl = BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy;
            var p = o.GetType().GetProperty(name, fl);
            if (p != null) return p.GetValue(o);
            var f = o.GetType().GetField(name, fl);
            if (f != null) return f.GetValue(o);
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static string DumpToString(object o, string label, int depth, int maxDepth)
    {
        var sw = new StringWriter();
        var old = Console.Out;
        Console.SetOut(sw);
        try
        {
            Dump(o, label, depth, maxDepth);
        }
        finally
        {
            Console.SetOut(old);
        }
        return sw.ToString();
    }

    private static void Dump(object? o, string label, int depth, int maxDepth)
    {
        var ind = new string(' ', depth * 2);
        if (o == null)
        {
            Console.WriteLine($"{ind}{label}: null");
            return;
        }

        var t = o.GetType();
        var tn = t.Name;

        if (PrimitiveNames.Contains(tn))
        {
            Console.WriteLine($"{ind}{label} = {o} ({tn})");
            return;
        }
        if (tn == "FName")
        {
            Console.WriteLine($"{ind}{label} = FName(\"{GetP(o, "Text")}\")");
            return;
        }
        if (tn.StartsWith("Lazy", StringComparison.Ordinal))
        {
            Console.WriteLine($"{ind}{label}: {tn} forcing...");
            object? val = null;
            try { val = GetP(o, "Value"); } catch (Exception ex) { Console.WriteLine($"{ind}  lazy fail: {ex.Message}"); }
            Dump(val, label + ".Value", depth + 1, maxDepth);
            return;
        }
        if (depth >= maxDepth)
        {
            Console.WriteLine($"{ind}{label}: {t.FullName} (depth cap)");
            return;
        }
        if (tn == "FPropertyTag")
        {
            var nm = GetP(o, "Name");
            string text = nm != null ? (GetP(nm, "Text")?.ToString() ?? nm.ToString() ?? "?") : "?";
            Console.WriteLine($"{ind}{label}: FPropertyTag '{text}'");
            Dump(GetP(o, "Tag"), "Tag", depth + 1, maxDepth);
            return;
        }
        if (tn.EndsWith("Property", StringComparison.Ordinal) && t.Namespace != null &&
            t.Namespace.Contains("Properties", StringComparison.Ordinal))
        {
            Console.WriteLine($"{ind}{label}: {tn}");
            Dump(GetP(o, "Value"), "Value", depth + 1, maxDepth);
            return;
        }
        if (tn == "FStructFallback")
        {
            Console.WriteLine($"{ind}{label}: FStructFallback");
            foreach (var p in EnumOf(o, "Properties"))
                Dump(p, "prop", depth + 1, maxDepth);
            return;
        }
        if (tn == "FScriptStruct")
        {
            Console.WriteLine($"{ind}{label}: FStructProp");
            Dump(GetP(o, "StructType"), "StructType", depth + 1, maxDepth);
            return;
        }
        if (tn == "UScriptArray")
        {
            int n = 0;
            foreach (var _ in EnumOf(o, "Properties")) n++;
            Console.WriteLine($"{ind}{label}: UScriptArray[{n}]");
            foreach (var p in EnumOf(o, "Properties"))
                Dump(p, "elem", depth + 1, maxDepth);
            return;
        }
        if (tn == "UScriptMap")
        {
            int mc = 0;
            foreach (var _ in EnumOf(o, "Properties")) mc++;
            if (mc == 0)
            {
                Console.WriteLine($"{ind}{label}: UScriptMap (Properties empty/null) {t.FullName}");
                foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Instance))
                {
                    object? fv;
                    try { fv = f.GetValue(o); } catch { continue; }
                    DumpMember(fv, f.Name, ind, depth, maxDepth);
                }
                foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    if (p.GetIndexParameters().Length > 0) continue;
                    object? pv;
                    try { pv = p.GetValue(o); } catch { continue; }
                    DumpMember(pv, p.Name, ind, depth, maxDepth);
                }
                return;
            }
            Console.WriteLine($"{ind}{label}: UScriptMap[{mc}]");
            foreach (var kv in EnumOf(o, "Properties"))
            {
                if (kv == null) continue;
                var k = GetP(kv, "Key") ?? GetP(kv, "Item1");
                var v = GetP(kv, "Value") ?? GetP(kv, "Item2");
                Dump(k, "key", depth + 1, maxDepth);
                Dump(v, "val", depth + 1, maxDepth);
            }
            return;
        }
        if (tn == "FPackageIndex")
        {
            Console.WriteLine($"{ind}{label}: FPackageIndex idx={GetP(o, "Index")}");
            Dump(GetP(o, "ResolvedObject"), "resolved", depth + 1, maxDepth);
            return;
        }
        if (tn.StartsWith("Resolved", StringComparison.Ordinal))
        {
            var nm = GetP(o, "Name");
            var cls = GetP(o, "Class");
            string name = nm != null ? (GetP(nm, "Text")?.ToString() ?? "?") : "?";
            string clsName = "?";
            if (cls != null)
            {
                var cn = GetP(cls, "Name");
                clsName = cn != null ? (GetP(cn, "Text")?.ToString() ?? "?") : "?";
            }
            Console.WriteLine($"{ind}{label}: {tn} class={clsName} name={name} idx={GetP(o, "ExportIndex")}");
            var obj = GetP(o, "Object");
            if (obj != null) Dump(obj, "object", depth + 1, maxDepth);
            return;
        }
        if (tn == "UObject" || (t.Namespace != null && t.Namespace.Contains(".Exports", StringComparison.Ordinal)))
        {
            Console.WriteLine($"{ind}{label}: {tn}");
            foreach (var p in EnumOf(o, "Properties"))
                Dump(p, "prop", depth + 1, maxDepth);
            return;
        }

        Console.WriteLine($"{ind}{label}: {t.FullName} (unhandled)");
        foreach (var m in t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            if (m.GetIndexParameters().Length > 0) continue;
            object? mv;
            try { mv = m.GetValue(o); } catch { continue; }
            DumpMember(mv, m.Name, ind, depth, maxDepth);
        }
        foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            object? fv;
            try { fv = f.GetValue(o); } catch { continue; }
            DumpMember(fv, f.Name, ind, depth, maxDepth);
        }
    }

    private static void DumpMember(object? mv, string name, string ind, int depth, int maxDepth)
    {
        if (mv == null)
        {
            Console.WriteLine($"{ind}  .{name} = null");
            return;
        }
        var mt = mv.GetType();
        if (mt.Name == "String")
        {
            Console.WriteLine($"{ind}  .{name} = \"{mv}\"");
            return;
        }
        if (mt.Name == "FName")
        {
            Console.WriteLine($"{ind}  .{name} = FName(\"{GetP(mv, "Text")}\")");
            return;
        }
        if (PrimitiveNames.Contains(mt.Name))
        {
            Console.WriteLine($"{ind}  .{name} = {mv} ({mt.Name})");
            return;
        }
        if (mv is IEnumerable en && mv is not string)
        {
            int n = 0;
            foreach (var _ in en) n++;
            Console.WriteLine($"{ind}  .{name}: seq[{n}] {mt.Name}");
            if (depth < maxDepth)
            {
                int i = 0;
                foreach (var item in en)
                {
                    if (i++ >= 8)
                    {
                        Console.WriteLine($"{ind}    ...");
                        break;
                    }
                    Dump(item, $"[{i - 1}]", depth + 2, maxDepth);
                }
            }
            return;
        }
        Console.WriteLine($"{ind}  .{name}: {mt.FullName}");
        if (depth < maxDepth)
            Dump(mv, $".{name}", depth + 1, maxDepth);
    }
}
