using System;
using System.IO;
using System.Linq;
using SifuMovesetEditor;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.UnrealTypes;

internal static class Program
{
    private static readonly (string path, string weapon)[] Targets =
    {
        ("Game/DB/AI/Archetypes/Sean/Sean_Combo_Phase2", "Staff"),
        ("Game/DB/AI/Archetypes/Yang/_DB/Phase1/Yang_P1_Combo", "BareHands"),
        ("Game/DB/AI/Archetypes/FireDisciple/Variations/Combo/FireDisciple_Base_Combo", "BareHands"),
        ("Game/DB/_MainChar/Combos/MainChar_ComboTree", "BareHands"),
    };

    private static int Main(string[] args)
    {
        var root = args.Length > 0 && args[0] != "--props" ? args[0]
            : @"C:\Users\Charles\Downloads\Sifu Modding\Movesets Prototypes\Moveset Maker\SifuMovesetEditor\bin\Debug\net10.0-windows\GameContent";

        if (args.Any(a => a == "--props"))
            return DumpProps(root);

        var parser = new AnimationParser();
        parser.Initialize(root, Path.Combine(root, "Content"));

        int fail = 0;
        int totalAction = 0, totalEvent = 0;
        int multiKeyRows = 0;

        foreach (var (path, weapon) in Targets)
        {
            Console.WriteLine($"== {path}");
            ComboGraph? graph;
            try
            {
                graph = parser.LoadComboTreeFromPath(path, weapon);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  FAIL: exception {ex.Message}");
                fail = 1;
                continue;
            }
            if (graph == null)
            {
                Console.WriteLine("  FAIL: graph null");
                fail = 1;
                continue;
            }

            int action = graph.Nodes.Count(n => n.IsActionNode);
            int evt = graph.Nodes.Count(n => n.IsEventNode);
            int root_ = graph.Nodes.Count(n => n.IsRoot);
            int redirect = graph.Nodes.Count(n => n.IsRedirect);
            int animless = graph.Nodes.Count(n => !n.IsRoot && !n.IsRedirect && string.IsNullOrEmpty(n.AnimPath));
            totalAction += action;
            totalEvent += evt;
            Console.WriteLine($"  nodes={graph.Nodes.Count} edges={graph.Edges.Count} root={root_} redirect={redirect} action={action} event={evt} animless={animless}");

            Console.WriteLine($"  conduits={graph.ConduitTargets.Count}");
            foreach (var kv in graph.ConduitTargets)
            {
                if (graph.Nodes.Any(n => n.TreeIndex == kv.Key))
                {
                    Console.WriteLine($"  FAIL: conduit key tree[{kv.Key}] still present as a graph node");
                    fail = 1;
                }
                foreach (var t in kv.Value)
                {
                    if (!graph.Nodes.Any(n => n.TreeIndex == t))
                    {
                        Console.WriteLine($"  FAIL: conduit tree[{kv.Key}] target tree[{t}] missing from graph");
                        fail = 1;
                    }
                }
                Console.WriteLine($"    conduit[{kv.Key}] -> [{string.Join(", ", kv.Value)}]");
            }

            var allRows = graph.TreeRows.Values.SelectMany(r => r).ToList();
            int condRows = allRows.Count(r => r.ConditionPresent);
            int paramRows = allRows.Count(r => r.ConditionParams.Count > 0);
            int caNodes = graph.TreeNodeConditions.Count;
            int caEntries = graph.TreeNodeConditions.Values.Sum(v => v.ConditionalAttacks.Count);
            int reqTags = graph.TreeNodeConditions.Values.Sum(v => v.RequiredTags.Count);
            Console.WriteLine($"  conds: rows={condRows} withParams={paramRows} nodeConds={caNodes} caEntries={caEntries} reqTags={reqTags}");
            foreach (var r in allRows.Where(r => r.ConditionParams.Count > 0).Take(3))
                Console.WriteLine($"    params [{r.ConditionClass}]: {string.Join("; ", r.ConditionParams)}");
            foreach (var kv in graph.TreeNodeConditions.Take(2))
                Console.WriteLine($"    nodeCond[{kv.Key}]: tags=[{string.Join(", ", kv.Value.RequiredTags)}] ca=[{string.Join(" | ", kv.Value.ConditionalAttacks.Select(c => $"{c.TagName}:{string.Join(",", c.AttackNames)}"))}]");

            foreach (var en in graph.StartEntries.Take(4))
            {
                var t0 = en.Transitions.FirstOrDefault();
                Console.WriteLine($"    ENTRY label='{en.Label}' ticket='{en.TicketEnumValue}' cond='{en.ConditionClass}' phase='{en.PhaseName}' flags={en.MCDominationFlags} vi={en.VanillaIndex} rows={en.Transitions.Count} r0=(prob={t0?.Probability} key={t0?.KeyByte} tgt={t0?.TargetTreeIndex})");
            }

            foreach (var en in graph.StartEntries)
            {
                foreach (var t in en.Transitions)
                {
                    if (t.KeyCount <= 1)
                    {
                        // Rows with no m_TargetNodes map parse to empty lists (KeyCount 1).
                        if (t.Keys.Count > 0 && t.Keys.Count != t.KeyCount)
                        {
                            Console.WriteLine($"  FAIL: single-key row entry='{en.Label}' idx={t.OriginalIndex} has {t.Keys.Count} parsed keys");
                            fail = 1;
                        }
                        continue;
                    }
                    multiKeyRows++;
                    if (t.Keys.Count != t.KeyCount
                        || t.KeyTargetTreeIndices.Count != t.KeyCount
                        || t.KeyTargetNodeIds.Count != t.KeyCount
                        || t.OriginalKeyTargetTreeIndices.Count != t.KeyCount)
                    {
                        Console.WriteLine($"  FAIL: multi-key row entry='{en.Label}' idx={t.OriginalIndex} "
                            + $"KeyCount={t.KeyCount} lists={t.Keys.Count}/{t.KeyTargetTreeIndices.Count}/{t.KeyTargetNodeIds.Count}/{t.OriginalKeyTargetTreeIndices.Count}");
                        fail = 1;
                    }
                }
            }

            foreach (var n in graph.Nodes.Where(n => n.IsActionNode || n.IsEventNode))
            {
                Console.WriteLine($"    {(n.IsActionNode ? "ACTION" : "EVENT  ")} tree={n.TreeIndex} '{n.DisplayName}' class='{n.ActionClass}' event='{n.EventName}' anim={(n.AnimPath.Length == 0 ? "<empty>" : n.AnimPath)}");
                if (n.IsActionNode && n.ActionClass.Length == 0)
                {
                    Console.WriteLine("  FAIL: action node without class name");
                    fail = 1;
                }
                if (n.IsEventNode && n.EventName.Length == 0)
                {
                    Console.WriteLine("  FAIL: event node without event name");
                    fail = 1;
                }
                if (n.DisplayName.Length == 0)
                {
                    Console.WriteLine("  FAIL: node without display name");
                    fail = 1;
                }
            }
        }

        Console.WriteLine($"totals: action={totalAction} event={totalEvent} multiKeyStartRows={multiKeyRows}");
        if (totalAction + totalEvent == 0)
        {
            Console.WriteLine("FAIL: no action/event nodes detected in any target");
            fail = 1;
        }

        Console.WriteLine(fail == 0 ? "ALL PASS" : "FAILED");
        return fail;
    }

    private static int DumpProps(string root)
    {
        foreach (var (path, _) in Targets)
        {
            string rel = path.StartsWith("Game/") ? path[5..] : path;
            string file = Path.Combine(root, "Content", rel.Replace('/', Path.DirectorySeparatorChar) + ".uasset");
            Console.WriteLine($"== {path} ({(File.Exists(file) ? "found" : "MISSING")})");
            if (!File.Exists(file)) continue;
            var asset = new UAsset(file, EngineVersion.VER_UE4_26, null, CustomSerializationFlags.None);
            for (int i = 0; i < asset.Exports.Count; i++)
            {
                if (asset.Exports[i] is not NormalExport ne || ne.Data == null) continue;
                bool isCombo = ne.Data.Any(p => p.Name?.Value?.ToString() == "m_Nodes");
                Console.WriteLine($"  export[{i}] size={ne.SerialSize} {(isCombo ? "(combo)" : "")}");
                foreach (var p in ne.Data)
                {
                    string extra = "";
                    if (p is UAssetAPI.PropertyTypes.Objects.ArrayPropertyData ap)
                        extra = $"[{ap.Value?.Length ?? 0}]";
                    else if (p is UAssetAPI.PropertyTypes.Objects.MapPropertyData mp)
                        extra = $"{{{mp.Value?.Count ?? 0}}}";
                    else if (p is UAssetAPI.PropertyTypes.Objects.IntPropertyData ip)
                        extra = $"={ip.Value}";
                    Console.WriteLine($"    {p.Name?.Value?.ToString()} : {p.GetType().Name}{extra}");
                }
                if (isCombo)
                {
                    var attacks = ne.Data.OfType<UAssetAPI.PropertyTypes.Objects.MapPropertyData>()
                        .FirstOrDefault(pp => pp.Name?.Value?.ToString() == "m_Attacks");
                    if (attacks?.Value != null)
                        foreach (var k in attacks.Value.Keys)
                            Console.WriteLine($"    m_Attacks key: {k.Name?.Value?.ToString()}");
                }
            }
        }
        return 0;
    }
}
