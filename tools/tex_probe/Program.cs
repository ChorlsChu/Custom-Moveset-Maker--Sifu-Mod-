using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Engine;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Assets.Objects.Properties;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Versions;
using CUE4Parse_Conversion.Animations;
using CUE4Parse_Conversion.Animations.PSA;
using CUE4Parse_Conversion.Meshes;
using CUE4Parse_Conversion.Meshes.PSK;
using CUE4Parse_Conversion.Textures;
using SkiaSharp;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;

internal static class Program
{
    private static string PngPathForUri(string uri)
        => Path.Combine(Path.GetTempPath(), "tex_probe_cache",
            Path.GetFileName(new Uri(uri).LocalPath));

    private static int RunBatch(DefaultFileProvider provider, string contentDir)
    {
        var meshes = new (string name, string path)[]
        {
            ("Grunt", "Game/Characters/PNJ/Grunt/M/Meshes/Hideout3/SK_Grunt_M_Hideout_03_Fighter_01"),
            ("FireDisciple", "Game/Characters/PNJ/Disicple/M/Meshes/SK_Disciple_M_Hideout02_Fighter_01"),
            ("FlashKick", "Game/Characters/PNJ/FlashKick/Meshes/Hideout03/SK_FlashKick_Hideout_03_FlashBlade_01"),
            ("BigGuy", "Game/Characters/PNJ/BigGuy/Meshes/Hideout03/SK_BigGuy_Hideout03_Fighter_01"),
            ("BodyGuard", "Game/Characters/PNJ/BodyGuards/M/Meshes/Hideout03/SK_BodyGuard_M_Hideout03_Fighter_01"),
            ("Servant", "Game/Characters/PNJ/Servant/M/Meshes/Hideout03/SK_Servant_M_Hideout03_Fighter_01"),
            ("Fajar", "Game/Characters/Boss/Fajar/Meshes/SK_Fajar_HD0"),
            ("Sean", "Game/Characters/Boss/Sean/Meshes/SK_Sean_HD0"),
            ("Kuroki", "Game/Characters/Boss/Kuroki/Meshes/SK_Kuroki_HD0"),
            ("Yang", "Game/Characters/Boss/Yang/Meshes/SK_Yang_Hideout00"),
            ("Fengjie", "Game/Characters/Boss/Fengjie/Meshes/SK_Fengjie_HD0"),
            ("MainChar", "Game/Characters/MainChar/M/Meshes/SK_M_MainChar_01"),
        };

        var resolver = new SifuMovesetEditor.MeshTextureResolver(
            provider, contentDir, Path.Combine(Path.GetTempPath(), "tex_probe_cache"));

        var allSw = System.Diagnostics.Stopwatch.StartNew();
        int failures = 0;
        foreach (var (name, path) in meshes)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var m = provider.SafeLoadPackageObject<USkeletalMesh>(path);
                if (m == null || !m.TryConvert(out var conv) || conv == null)
                { Console.WriteLine($"{name,-14} LOAD/CONVERT FAILED"); failures++; continue; }
                var lod = conv.LODs.FirstOrDefault() as CSkelMeshLod;
                if (lod == null) { Console.WriteLine($"{name,-14} NO LOD"); failures++; continue; }

                var input = new List<(string material, int firstIndex, int numFaces)>();
                foreach (var s in lod.Sections.Value)
                    input.Add((s.Material?.ToString() ?? "", s.FirstIndex, s.NumFaces));

                int covered = 0;
                foreach (var t in input) covered += t.Item3 * 3;
                var groups = resolver.ResolveTextureGroups(input);
                int textured = groups?.Count(g => g.png != null) ?? 0;
                int totalIndices = lod.Indices.Value.Length;
                bool ok = groups != null && covered == totalIndices;
                if (!ok || groups == null || textured == 0) failures++;
                Console.WriteLine($"{name,-14} sections={input.Count,-3} textured={textured,-3} covered={covered,-7}/{totalIndices,-7} {(ok ? "OK" : "MISMATCH")} {sw.ElapsedMilliseconds}ms");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{name,-14} EXCEPTION {ex.Message}");
                failures++;
            }
        }

        var warmSw = System.Diagnostics.Stopwatch.StartNew();
        foreach (var (name, path) in meshes.Take(3))
        {
            var m = provider.SafeLoadPackageObject<USkeletalMesh>(path);
            if (m == null || !m.TryConvert(out var conv) || conv == null) continue;
            var lod = conv.LODs.FirstOrDefault() as CSkelMeshLod;
            if (lod == null) continue;
            var input = new List<(string material, int firstIndex, int numFaces)>();
            foreach (var s in lod.Sections.Value)
                input.Add((s.Material?.ToString() ?? "", s.FirstIndex, s.NumFaces));
            resolver.ResolveTextureGroups(input);
        }
        Console.WriteLine($"warm 3 meshes: {warmSw.ElapsedMilliseconds}ms");
        Console.WriteLine($"total {allSw.ElapsedMilliseconds}ms, failures={failures}");
        return failures == 0 ? 0 : 8;
    }

    private static int Main(string[] args)
    {
        var root = args.Length > 0 ? args[0]
            : @"C:\Users\Charles\Downloads\Sifu Modding\Movesets Prototypes\Moveset Maker\SifuMovesetEditor\bin\Debug\net10.0-windows\GameContent";
        var meshPath = args.Length > 1 ? args[1]
            : "Game/Characters/PNJ/Servant/M/Meshes/Hideout03/SK_Servant_M_Hideout03_Fighter_01";

        var provider = new DefaultFileProvider(root, SearchOption.AllDirectories,
            new VersionContainer(EGame.GAME_UE4_26), StringComparer.OrdinalIgnoreCase);
        provider.Initialize();
        Console.WriteLine($"ProjectName={provider.ProjectName}");

        if (args.Length > 1 && args[1] == "--batch")
            return RunBatch(provider, Path.Combine(root, "Content"));

        var mesh = provider.SafeLoadPackageObject<USkeletalMesh>(meshPath);
        if (mesh == null) { Console.WriteLine("MESH LOAD FAILED"); return 1; }
        Console.WriteLine($"mesh loaded: {mesh.GetType().Name}");

        Console.WriteLine("\n== USkeletalMesh runtime properties ==");
        foreach (var p in mesh.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            Console.WriteLine($"  {p.PropertyType.Name} {p.Name}");
        Console.WriteLine("== USkeletalMesh export Properties list ==");
        foreach (var pr in mesh.Properties)
            Console.WriteLine($"  {pr.Name} ({pr.GetType().Name})");

        Console.WriteLine("== SkeletalMaterials ==");
        if (GetMemberValue(mesh, "SkeletalMaterials") is Array smArr)
        {
            for (int i = 0; i < smArr.Length; i++)
            {
                var sm = smArr.GetValue(i)!;
                if (i == 0)
                    foreach (var fd in sm.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
                        Console.WriteLine($"    field: {fd.FieldType.Name} {fd.Name}");
                var iface = GetMemberValue(sm, "MaterialInterface");
                Console.WriteLine($"  [{i}] {iface?.GetType().Name}: {Describe(iface)}");
            }
        }
        Console.WriteLine("== Materials (ResolvedObject[]) ==");
        if (GetMemberValue(mesh, "Materials") is Array mArr)
        {
            for (int i = 0; i < mArr.Length; i++)
                Console.WriteLine($"  [{i}] {Describe(mArr.GetValue(i))}");
        }

        Console.WriteLine("== StaticMaterials via UAssetAPI ==");
        var contentDir = Path.Combine(root, "Content");
        var meshFile = Path.Combine(contentDir, meshPath.Substring("Game/".Length).Replace('/', Path.DirectorySeparatorChar) + ".uasset");
        var slotPaths = new List<string>();
        try
        {
            var ua = new UAsset(meshFile, EngineVersion.VER_UE4_26, null, CustomSerializationFlags.None);
            foreach (var exp in ua.Exports)
            {
                if (exp is not NormalExport ne || ne.Data == null) continue;
                foreach (var p in ne.Data)
                {
                    if (p is not ArrayPropertyData ap || p.Name?.Value?.ToString() != "StaticMaterials") continue;
                    int i = 0;
                    foreach (var e in ap.Value ?? Array.Empty<PropertyData>())
                    {
                        if (e is not StructPropertyData sp || sp.Value == null) { i++; continue; }
                        string? path = null; string? slot = null;
                        foreach (var pp in sp.Value)
                        {
                            if (pp is ObjectPropertyData op && pp.Name?.Value?.ToString() == "MaterialInterface")
                                path = ResolvePackagePath(ua, op.Value);
                            if (pp is NamePropertyData np2 && pp.Name?.Value?.ToString() == "MaterialSlotName")
                                slot = np2.Value?.ToString();
                        }
                        Console.WriteLine($"  [{i}] slot={slot} -> {path ?? "<none>"}");
                        if (path != null) slotPaths.Add(path);
                        i++;
                    }
                }
            }
        }
        catch (Exception ex) { Console.WriteLine("StaticMaterials dump error: " + ex.Message); }

        Console.WriteLine("\n== mesh.TryConvert ==");
        if (!mesh.TryConvert(out var converted) || converted == null)
        { Console.WriteLine("TryConvert FAILED"); return 2; }
        Console.WriteLine($"LODs={converted.LODs.Count}");
        var lod = converted.LODs.FirstOrDefault() as CSkelMeshLod;
        if (lod == null) { Console.WriteLine("no CSkelMeshLod"); return 3; }

        Console.WriteLine("== CSkelMeshLod members ==");
        foreach (var m in lod.GetType().GetMembers(BindingFlags.Public | BindingFlags.Instance))
            Console.WriteLine($"  {m.MemberType}: {m}");
        DumpCollectionElementType(lod, "Wedges");
        DumpCollectionElementType(lod, "Points");
        DumpCollectionElementType(lod, "Faces");
        DumpCollectionElementType(lod, "SecIndices");
        DumpCollectionElementType(lod, "InfBones");

        Console.WriteLine("== Sections (CMeshSection[]) ==");
        if (GetMemberValue(lod, "Sections") is System.Lazy<CMeshSection[]> lazySec)
        {
            var secs = lazySec.Value;
            Console.WriteLine($"  count={secs.Length}");
            foreach (var s in secs.Take(3))
            {
                Console.WriteLine("  section fields:");
                foreach (var fd in s.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
                    Console.WriteLine($"    {fd.FieldType.Name} {fd.Name} = {fd.GetValue(s)}");
            }
            if (secs.Length > 3)
            {
                var s = secs[3];
                foreach (var fd in s.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
                    Console.WriteLine($"    [3] {fd.FieldType.Name} {fd.Name} = {fd.GetValue(s)}");
            }
        }
        else Console.WriteLine("  Sections not Lazy<CMeshSection[]> or absent");

        var lazyIdx = GetMemberValue(lod, "Indices") as System.Lazy<uint[]>;
        if (lazyIdx != null)
            Console.WriteLine($"  Indices count={lazyIdx.Value.Length}, first=[{string.Join(",", lazyIdx.Value.Take(6))}]");

        var faces = GetMemberValue(lod, "Faces") as System.Collections.IEnumerable;
        if (faces != null)
        {
            int n = 0;
            foreach (var f in faces)
            {
                if (n++ >= 3) break;
                Console.WriteLine("  face sample: " +
                    string.Join(", ", f!.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance)
                        .Select(fd => $"{fd.Name}={fd.GetValue(f)}")));
            }
        }
        var wedges = GetMemberValue(lod, "Wedges") as System.Collections.IEnumerable;
        if (wedges != null)
        {
            int n = 0;
            foreach (var w in wedges)
            {
                if (n++ >= 3) break;
                Console.WriteLine("  wedge sample: " +
                    string.Join(", ", w!.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance)
                        .Select(fd => $"{fd.Name}={fd.GetValue(w)}")));
            }
        }

        Console.WriteLine("\n== resolver end-to-end (shipped MeshTextureResolver) ==");
        var resolver = new SifuMovesetEditor.MeshTextureResolver(
            provider, contentDir, Path.Combine(Path.GetTempPath(), "tex_probe_cache"));

        var input = new List<(string material, int firstIndex, int numFaces)>();
        if (GetMemberValue(lod, "Sections") is System.Lazy<CMeshSection[]> secLazy)
            foreach (var s in secLazy.Value)
                input.Add((s.Material?.ToString() ?? "", s.FirstIndex, s.NumFaces));

        var sw1 = System.Diagnostics.Stopwatch.StartNew();
        var groups = resolver.ResolveTextureGroups(input);
        sw1.Stop();
        Console.WriteLine($"cold: groups={groups?.Count} in {sw1.ElapsedMilliseconds}ms");

        var sw2 = System.Diagnostics.Stopwatch.StartNew();
        var groups2 = resolver.ResolveTextureGroups(input);
        sw2.Stop();
        Console.WriteLine($"warm: groups={groups2?.Count} in {sw2.ElapsedMilliseconds}ms");

        if (groups == null || groups.Count == 0) { Console.WriteLine("NO GROUPS"); return 5; }

        int covered = 0;
        int textured = 0;
        foreach (var g in groups)
        {
            covered += g.count;
            if (g.png != null)
            {
                textured++;
                var fs = new FileInfo(PngPathForUri(g.png));
                if (!fs.Exists || fs.Length == 0) { Console.WriteLine($"MISSING PNG: {g.png}"); return 6; }
            }
        }
        Console.WriteLine($"textured groups={textured}/{groups.Count}, covered={covered}, indices={lazyIdx.Value.Length}");
        if (covered != lazyIdx.Value.Length) { Console.WriteLine("COVERAGE MISMATCH"); return 7; }

        var firstTex = groups.FirstOrDefault(g => g.png != null);
        if (firstTex != null)
        {
            var outPng = Path.Combine(Path.GetTempPath(), "tex_probe_out.png");
            File.Copy(PngPathForUri(firstTex.png!), outPng, true);
            Console.WriteLine($"sample texture copied -> {outPng}");
        }

        int inTex = 0;
        foreach (var g in groups)
            if (g.png != null && !File.Exists(PngPathForUri(g.png))) inTex++;
        Console.WriteLine(inTex == 0 ? "ALL TEXTURE FILES PRESENT" : "TEXTURE FILES MISSING");
        return 0;
    }

    private static void DumpCollectionElementType(object obj, string name)
    {
        var f = obj.GetType().GetField(name) ?? null;
        var p = obj.GetType().GetProperty(name);
        object? val = f != null ? f.GetValue(obj) : p?.GetValue(obj);
        if (val == null) { Console.WriteLine($"  {name}: <absent>"); return; }
        var t = val.GetType();
        var elem = t.IsArray ? t.GetElementType()! :
            t.GetGenericArguments().FirstOrDefault() ?? t;
        Console.WriteLine($"  {name}: {t.Name} of {elem.Name}");
        foreach (var m in elem.GetMembers(BindingFlags.Public | BindingFlags.Instance).Take(20))
            Console.WriteLine($"      {m.MemberType}: {m}");
    }

    private static object? GetMemberValue(object obj, string name)
    {
        var t = obj.GetType();
        var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (p != null) return p.GetValue(obj);
        var f = t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        return f?.GetValue(obj);
    }

    private static string? Describe(object? o)
    {
        if (o == null) return "<null>";
        var obj = GetMemberValue(o, "Object");
        var path = GetMemberValue(obj ?? o, "Path") ?? GetMemberValue(obj ?? o, "Name");
        var typeName = GetMemberValue(obj ?? o, "ExportType") ?? "";
        return $"{o.GetType().Name} Object={(obj?.GetType().Name ?? "null")} Path={path} Type={typeName}";
    }

    private static List<string> CollectMaterialRels(string contentDir, string meshPath)
    {
        var result = new List<string>();
        try
        {
            var meshFile = Path.Combine(contentDir, meshPath.Substring("Game/".Length).Replace('/', Path.DirectorySeparatorChar) + ".uasset");
            var bytes = File.ReadAllBytes(meshFile);
            var text = System.Text.Encoding.ASCII.GetString(bytes);
            foreach (System.Text.RegularExpressions.Match m in
                     System.Text.RegularExpressions.Regex.Matches(text, @"/Game/[^ \x00]{5,120}"))
            {
                var v = m.Value;
                if (v.Contains("/Materials/MI_") && !result.Contains(v)) result.Add(v);
            }
        }
        catch (Exception ex) { Console.WriteLine("collect error: " + ex.Message); }
        return result;
    }

    private static string? FindBaseColor(string contentDir, string gamePath)
    {
        string? current = gamePath;
        var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int depth = 0; depth < 6 && !string.IsNullOrEmpty(current); depth++)
        {
            var file = Path.Combine(contentDir, current!.Substring("/Game/".Length).Replace('/', Path.DirectorySeparatorChar) + ".uasset");
            if (!File.Exists(file)) break;
            UAsset asset;
            try { asset = new UAsset(file, EngineVersion.VER_UE4_26, null, CustomSerializationFlags.None); }
            catch { break; }

            string? parent = null;
            foreach (var exp in asset.Exports)
            {
                if (exp is not NormalExport ne || ne.Data == null) continue;
                foreach (var p in ne.Data)
                {
                    if (p is ObjectPropertyData op && (p.Name?.Value?.ToString() == "Parent"))
                        parent = ResolvePackagePath(asset, op.Value);
                    if (p is not ArrayPropertyData ap || p.Name?.Value?.ToString() != "TextureParameterValues")
                        continue;
                    foreach (var e in ap.Value ?? Array.Empty<PropertyData>())
                    {
                        if (e is not StructPropertyData sp || sp.Value == null) continue;
                        string? pname = null; string? tex = null;
                        foreach (var pp in sp.Value)
                        {
                            if (pp is StructPropertyData pi && pi.Name?.Value?.ToString() == "ParameterInfo")
                        foreach (var pip in pi.Value ?? new())
                            if (pip.Name?.Value?.ToString() == "Name")
                                pname = pip switch
                                {
                                    NamePropertyData np => np.Value?.ToString(),
                                    StrPropertyData spv => spv.Value?.ToString(),
                                    _ => null
                                };
                            if (pp is ObjectPropertyData tp && pp.Name?.Value?.ToString() == "ParameterValue")
                                tex = ResolvePackagePath(asset, tp.Value);
                        }
                        if (!string.IsNullOrEmpty(pname) && !string.IsNullOrEmpty(tex) && !merged.ContainsKey(pname))
                            merged[pname] = tex;
                    }
                }
            }
            current = parent;
        }

        if (merged.TryGetValue("BaseColor", out var bc)) return bc;
        var cSuffixed = merged.Values.FirstOrDefault(v => v.EndsWith("_C", StringComparison.OrdinalIgnoreCase));
        return cSuffixed;
    }

    private static string? ResolvePackagePath(UAsset asset, FPackageIndex? idx)
    {
        if (idx == null || idx.Index >= 0) return null;
        int i = -(idx.Index + 1);
        if (i < 0 || i >= asset.Imports.Count) return null;
        var imp = asset.Imports[i];
        var o = imp.OuterIndex.Index;
        if (o >= 0 || -(o + 1) >= asset.Imports.Count) return null;
        var pkg = asset.Imports[-(o + 1)].ObjectName.Value?.ToString();
        if (string.IsNullOrEmpty(pkg) || !pkg!.StartsWith("/Game/")) return null;
        return pkg;
    }

    private static bool TryDecodePng(UObject tex, DefaultFileProvider provider, string outPath)
    {
        try
        {
            Console.WriteLine("ETexturePlatform: " + string.Join(", ", Enum.GetNames(typeof(ETexturePlatform))));
            var plat = Enum.GetNames(typeof(ETexturePlatform)).Contains("P3DPC")
                ? Enum.Parse<ETexturePlatform>("P3DPC")
                : Enum.GetValues<ETexturePlatform>()[0];

            var ctex = TextureDecoder.Decode((UTexture)tex, plat);
            if (ctex == null) { Console.WriteLine("Decode returned null"); return false; }

            Console.WriteLine("CTexture fields:");
            foreach (var f in ctex.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
                Console.WriteLine($"  {f.FieldType.Name} {f.Name}");
            Console.WriteLine("CTexture properties:");
            foreach (var p in ctex.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                Console.WriteLine($"  {p.PropertyType.Name} {p.Name}");

            using var sk = TextureEncoder.ToSkBitmap(ctex);
            if (sk == null) { Console.WriteLine("ToSkBitmap null"); return false; }
            Console.WriteLine($"SKBitmap {sk.Width}x{sk.Height} colorType={sk.ColorType}");
            using var img = SKImage.FromBitmap(sk);
            using var png = img.Encode(SKEncodedImageFormat.Png, 90);
            using var fs = File.Create(outPath);
            png.SaveTo(fs);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine("decode probe error: " + ex);
            return false;
        }
    }
}
