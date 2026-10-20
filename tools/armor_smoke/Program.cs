using System.IO;
using SifuMovesetEditor;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;

// Smoke-test ArmorNotifyReader against known vanilla anims.
// Usage: armor_smoke <contentDir> [debug <animPath>]

string contentDir = args.Length > 0 ? args[0]
    : @"C:\Users\Charles\Downloads\Sifu Modding\Unreal Pak Extracter and Creator\extractedPaks\pakchunk0-WindowsNoEditor\Sifu\Content";

if (args.Length >= 3 && args[1] == "debug") return DebugDump(contentDir, args[2]);

Console.WriteLine($"contentDir=[{contentDir}] exists={Directory.Exists(contentDir)}");
Console.WriteLine($"rel=[{VoiceArchetypeFixer.NormalizeRel("Game/Animations/BigGuy/SpecialMove/FarGrab/BigGuy_specialMove_FarGrab_60fps")}]");

int pass = 0, fail = 0;

// Tier assertions only; timing must be present (vanilla always ships it) but values
// aren't hardcoded - the PASS line prints them for eyeballing.
void Check(string label, string animPath, params ArmorTier[] expectedTiers)
{
    var file = ArmorNotifyReader.ResolveAnimFile(contentDir, animPath);
    if (file == null) { Console.WriteLine($"FAIL {label}: file not found ({animPath})"); fail++; return; }
    var got = ArmorNotifyReader.Read(file);
    if (got == null) { Console.WriteLine($"FAIL {label}: Read returned null"); fail++; return; }
    if (got.Count != expectedTiers.Length)
    {
        Console.WriteLine($"FAIL {label}: {got.Count} notify(s), expected {expectedTiers.Length}");
        foreach (var a in got) Console.WriteLine($"  got: {a.Tier} {a.StartTime} -> {a.EndTime} dur={a.Duration}");
        fail++; return;
    }
    for (int i = 0; i < expectedTiers.Length; i++)
    {
        var a = got[i];
        if (a.Tier != expectedTiers[i])
        {
            Console.WriteLine($"FAIL {label}[{i}]: tier {a.Tier}, expected {expectedTiers[i]}");
            fail++; return;
        }
        if (a.StartTime == null || a.EndTime == null)
        {
            Console.WriteLine($"FAIL {label}[{i}]: timing missing (start={a.StartTime}, end={a.EndTime})");
            fail++; return;
        }
        // LinkValue semantics: start < end, non-negative, and end-start == Duration.
        if (a.StartTime < 0 || a.EndTime <= a.StartTime)
        {
            Console.WriteLine($"FAIL {label}[{i}]: bad window {a.StartTime} -> {a.EndTime}");
            fail++; return;
        }
        if (a.Duration != null && Math.Abs((a.EndTime.Value - a.StartTime.Value) - a.Duration.Value) > 0.01f)
        {
            Console.WriteLine($"FAIL {label}[{i}]: end-start != dur ({a.StartTime} -> {a.EndTime}, dur={a.Duration})");
            fail++; return;
        }
    }
    var timing = string.Join("; ", got.Select(a =>
        $"{a.Tier} {a.StartTime?.ToString("0.###") ?? "?"}s -> {a.EndTime?.ToString("0.###") ?? "?"}s (dur {a.Duration?.ToString("0.###") ?? "?"}s)"));
    Console.WriteLine($"PASS {label}: {got.Count} notify(s) — {timing}");
    pass++;
}

void CheckNone(string label, string animPath)
{
    Check(label, animPath); // zero expected
}

// Known armored (from the vanilla scan):
Check("BigGuy FarGrab (expect Super+Regular)",
    "Game/Animations/BigGuy/SpecialMove/FarGrab/BigGuy_specialMove_FarGrab_60fps",
    ArmorTier.Super, ArmorTier.Resilience);
Check("BigGuy Combo01_Hook (expect Super+Regular)",
    "Game/Animations/BigGuy/Attacks/Barehands/Combo01/BigGuy_attack_barehands_Combo01_Hook",
    ArmorTier.Super, ArmorTier.Resilience);
Check("FlashKick tornado_kick (expect Regular)",
    "Game/Animations/FlashKick/Attacks/Barehands/Light/Flashkick_attack_barehands_tornado_kick_60fps",
    ArmorTier.Resilience);
Check("Grunt 540Kick (expect Regular)",
    "Game/Animations/Grunt/Attacks/Barehands/540Kick/Grunt_Attack_Barehands_540Kick",
    ArmorTier.Resilience);

// Known clean:
CheckNone("Grunt Hook (expect none)",
    "Game/Animations/Grunt/Attacks/Barehands/Hook/Grunt_Attack_barehands_Hook");

// Missing file -> null
var missing = ArmorNotifyReader.Read(Path.Combine(contentDir, "Animations\\__no_such_anim__.uasset"));
Console.WriteLine(missing == null ? "PASS missing file -> null" : "FAIL missing file not null");
if (missing == null) pass++; else fail++;

Console.WriteLine($"\n{pass} passed, {fail} failed");
return fail == 0 ? 0 : 1;

int DebugDump(string cdir, string animPath)
{
    string rel = VoiceArchetypeFixer.NormalizeRel(animPath);
    string file = Path.Combine(cdir, rel.Replace('/', Path.DirectorySeparatorChar) + ".uasset");
    Console.WriteLine($"file={file} exists={File.Exists(file)}");
    if (!File.Exists(file)) return 1;

    var asset = new UAsset(file, EngineVersion.VER_UE4_26, null, CustomSerializationFlags.None);
    Console.WriteLine($"exports={asset.Exports.Count} imports={asset.Imports.Count}");

    var notifies = VoiceArchetypeFixer.FindNotifies(asset);
    if (notifies?.Value == null)
    {
        Console.WriteLine("FindNotifies: NULL (no Notifies array or empty)");
        foreach (var e2 in asset.Exports)
        {
            if (e2 is not NormalExport ne2 || ne2.Data == null) continue;
            var arr = ne2.Data.FirstOrDefault(p => p.Name?.Value?.ToString() == "Notifies") as ArrayPropertyData;
            if (arr != null)
                Console.WriteLine($"  export[{asset.Exports.IndexOf(e2)}] Notifies value={(arr.Value?.Length.ToString() ?? "null")}");
        }
        return 1;
    }
    Console.WriteLine($"FindNotifies: {notifies.Value.Length} entries");

    Console.WriteLine("--- all exports ---");
    for (int ei = 0; ei < asset.Exports.Count; ei++)
    {
        var e = asset.Exports[ei];
        int ci = e.ClassIndex?.Index ?? 0;
        string cls = ci >= 0 ? $"local:{ci}" : (-(ci + 1) < asset.Imports.Count ? asset.Imports[-(ci + 1)].ObjectName?.Value?.ToString() ?? "?" : "OOB");
        Console.WriteLine($"  exp[{ei}] class={cls} name={e.ObjectName}"
            + (e is NormalExport ne3 && ne3.Data != null
                ? " data[" + string.Join(", ", ne3.Data.Select(d => d.Name?.Value?.ToString())) + "]"
                : ""));
    }

    for (int i = 0; i < notifies.Value.Length; i++)
    {
        var entry = notifies.Value[i];
        Console.WriteLine($"entry[{i}]: {entry.GetType().Name}");
        if (entry is not StructPropertyData se || se.Value == null) continue;
        foreach (var p in se.Value)
            Console.WriteLine($"    prop: {p.Name} : {p.GetType().Name}");

        var notify = se.Value.FirstOrDefault(p => p.Name?.Value?.ToString() == "Notify") as ObjectPropertyData;
        var nsc = se.Value.FirstOrDefault(p => p.Name?.Value?.ToString() == "NotifyStateClass") as ObjectPropertyData;
        var nn = se.Value.FirstOrDefault(p => p.Name?.Value?.ToString() == "NotifyName") as NamePropertyData;
        string nnStr = nn?.Value?.Value?.ToString() ?? "?";
        if (nnStr == "OrderServiceNotifyState")
        {
            Console.WriteLine($"    [OrderService] FULL props:");
            foreach (var p in se.Value)
            {
                string v = p switch
                {
                    FloatPropertyData f => f.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    IntPropertyData ip => ip.Value.ToString(),
                    BoolPropertyData b => b.Value.ToString(),
                    NamePropertyData np => np.Value?.Value?.ToString() ?? "null",
                    ObjectPropertyData op => $"idx={op.Value?.Index}",
                    StructPropertyData sp => "struct[" + string.Join(", ",
                        (sp.Value ?? new List<PropertyData>()).Select(q =>
                            q.Name + "=" + (q is FloatPropertyData fq ? fq.Value.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)
                                : q is IntPropertyData iq ? iq.Value.ToString()
                                : q is NamePropertyData nq ? nq.Value?.Value?.ToString()
                                : q is ObjectPropertyData oq ? $"idx={oq.Value?.Index}"
                                : q.GetType().Name))) + "]",
                    _ => p.GetType().Name,
                };
                Console.WriteLine($"      {p.Name} : {v}");
            }
        }
        if (notify?.Value == null || notify.Value.Index <= 0)
        {
            Console.WriteLine($"    -> no Notify ptr (Notify={notify?.Value?.Index.ToString() ?? "null"}"
                + $" NotifyStateClass={nsc?.Value?.Index.ToString() ?? "null"}"
                + $" NotifyName={nnStr})");
            continue;
        }
        int expIdx = notify.Value.Index - 1;
        if (expIdx < 0 || expIdx >= asset.Exports.Count) { Console.WriteLine($"    -> expIdx {expIdx} OOB"); continue; }
        var exp = asset.Exports[expIdx];
        Console.WriteLine($"    export[{expIdx}]: classIdx={exp.ClassIndex?.Index} objName={exp.ObjectName}");
        int classIdx = exp.ClassIndex?.Index ?? 0;
        if (classIdx < 0)
        {
            int impIdx = -(classIdx + 1);
            string cls = impIdx >= 0 && impIdx < asset.Imports.Count
                ? asset.Imports[impIdx].ObjectName?.Value?.ToString() ?? "?" : "OOB";
            Console.WriteLine($"    class import[{impIdx}]={cls}");
            if (exp is NormalExport ne && ne.Data != null)
            {
                foreach (var dp in ne.Data)
                    Console.WriteLine($"      data: {dp.Name} : {dp.GetType().Name}" +
                        (dp is ObjectPropertyData op ? $" -> {op.Value?.Index}" : ""));
                var svc = ne.Data.FirstOrDefault(p => p.Name?.Value?.ToString() == "m_ServiceClass") as ObjectPropertyData;
                if (svc?.Value != null && svc.Value.Index < 0)
                {
                    int si = -(svc.Value.Index + 1);
                    Console.WriteLine($"      m_ServiceClass -> import[{si}]={asset.Imports[si].ObjectName}");
                }
            }
        }
    }
    return 0;
}
