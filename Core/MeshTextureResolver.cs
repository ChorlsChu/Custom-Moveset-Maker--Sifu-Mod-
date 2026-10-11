using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse_Conversion.Textures;
using SkiaSharp;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;

namespace SifuMovesetEditor;

public class MeshMaterialGroup
{
    public string? png { get; set; }
    public int start { get; set; }
    public int count { get; set; }
}

// Resolves mesh sections to BaseColor texture PNGs (disk-cached, memory LRU) for
// the model picker thumbnails and the animation preview.
public sealed class MeshTextureResolver
{
    private const int PngMemoryEntries = 48;
    private const int MaxTextureSize = 1024;

    // Virtual host mapped to the Thumbnails dir via SetVirtualHostNameToFolderMapping
    // (file:// image loads fail CORS because three.js sets crossOrigin=anonymous).
    public const string TextureHost = "sifu-assets.local";

    private readonly DefaultFileProvider _provider;
    private readonly string _contentDir;
    private readonly string _pngCacheDir;

    private readonly object _sync = new();
    private readonly Dictionary<string, string?> _baseColorByMaterial = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _failedTextures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, byte[]> _pngMemory = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _pngOrder = new();

    public MeshTextureResolver(DefaultFileProvider provider, string contentDir, string pngCacheDir)
    {
        _provider = provider;
        _contentDir = contentDir;
        _pngCacheDir = pngCacheDir;
    }

    public void Clear()
    {
        lock (_sync)
        {
            _baseColorByMaterial.Clear();
            _failedTextures.Clear();
            _pngMemory.Clear();
            _pngOrder.Clear();
        }
    }

    public List<MeshMaterialGroup>? ResolveTextureGroups(IReadOnlyList<(string material, int firstIndex, int numFaces)> sections)
    {
        if (sections.Count == 0) return null;

        var groups = new List<MeshMaterialGroup>(sections.Count);
        foreach (var (material, firstIndex, numFaces) in sections)
        {
            string? png = null;
            var matPath = MaterialGamePath(material);
            if (matPath != null)
            {
                var texPath = FindBaseColorTexture(matPath);
                if (texPath != null)
                    png = GetTexturePngUri(texPath);
            }
            groups.Add(new MeshMaterialGroup { png = png, start = firstIndex, count = numFaces * 3 });
        }
        return groups;
    }

    // "MaterialInstanceConstant'GameContent/Content/Characters/.../MI_X.MI_X'" -> "/Game/Characters/.../MI_X"
    private static string? MaterialGamePath(string resolved)
    {
        if (string.IsNullOrEmpty(resolved)) return null;
        var inner = resolved;
        var quote = resolved.IndexOf('\'');
        if (quote >= 0 && resolved.EndsWith("'") && resolved.Length > quote + 1)
            inner = resolved.Substring(quote + 1, resolved.Length - quote - 2);

        const string prefix = "GameContent/Content/";
        if (!inner.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var pkg = inner.Substring(prefix.Length);
        var dot = pkg.LastIndexOf('.');
        if (dot > 0) pkg = pkg.Substring(0, dot);
        return "/Game/" + pkg;
    }

    private string? FindBaseColorTexture(string materialPath)
    {
        lock (_sync)
        {
            if (_baseColorByMaterial.TryGetValue(materialPath, out var cached)) return cached;
        }
        var resolved = ComputeBaseColor(materialPath);
        lock (_sync) _baseColorByMaterial[materialPath] = resolved;
        return resolved;
    }

    // Walk the MI Parent chain merging TextureParameterValues; prefer BaseColor, else any _C texture.
    private string? ComputeBaseColor(string gamePath)
    {
        string? current = gamePath;
        var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int depth = 0; depth < 6 && !string.IsNullOrEmpty(current); depth++)
        {
            var file = Path.Combine(_contentDir, current!.Substring("/Game/".Length).Replace('/', Path.DirectorySeparatorChar) + ".uasset");
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
                    if (p is ObjectPropertyData op && p.Name?.Value?.ToString() == "Parent")
                        parent = ResolvePackagePath(asset, op.Value);
                    if (p is not ArrayPropertyData ap || p.Name?.Value?.ToString() != "TextureParameterValues")
                        continue;
                    foreach (var e in ap.Value ?? Array.Empty<PropertyData>())
                    {
                        if (e is not StructPropertyData sp || sp.Value == null) continue;
                        string? pname = null;
                        string? tex = null;
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
                        if (!string.IsNullOrEmpty(pname) && !string.IsNullOrEmpty(tex) && !merged.ContainsKey(pname!))
                            merged[pname!] = tex!;
                    }
                }
            }
            current = parent;
        }

        if (merged.TryGetValue("BaseColor", out var bc)) return bc;
        return merged.Values.FirstOrDefault(v => v.EndsWith("_C", StringComparison.OrdinalIgnoreCase));
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

    private static string TexHash(string texPath)
    {
        using var sha = System.Security.Cryptography.SHA1.Create();
        var hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(texPath.ToLowerInvariant()));
        return string.Concat(hash.Select(b => b.ToString("x2")));
    }

    private string PngCachePath(string texPath)
        => Path.Combine(_pngCacheDir, TexHash(texPath) + ".png");

    private string? GetTexturePngUri(string texPath)
    {
        var bytes = GetTexturePng(texPath);
        if (bytes == null) return null;
        return $"https://{TextureHost}/tex/{TexHash(texPath)}.png";
    }

    private byte[]? GetTexturePng(string texPath)
    {
        lock (_sync)
        {
            if (_pngMemory.TryGetValue(texPath, out var hit)) return hit;
            if (_failedTextures.Contains(texPath)) return null;
        }

        var file = PngCachePath(texPath);
        byte[]? bytes = null;
        try
        {
            if (File.Exists(file))
            {
                bytes = File.ReadAllBytes(file);
            }
            else
            {
                bytes = DecodeTexturePng(texPath);
                if (bytes != null)
                {
                    Directory.CreateDirectory(_pngCacheDir);
                    File.WriteAllBytes(file, bytes);
                }
            }
        }
        catch { bytes = null; }

        lock (_sync)
        {
            if (bytes != null) RememberPng(texPath, bytes);
            else _failedTextures.Add(texPath);
        }
        return bytes;
    }

    private void RememberPng(string texPath, byte[] bytes)
    {
        if (_pngMemory.TryGetValue(texPath, out var existing))
        {
            _pngMemory[texPath] = bytes;
            _pngOrder.Remove(texPath);
            _pngOrder.AddFirst(texPath);
            return;
        }
        _pngMemory[texPath] = bytes;
        _pngOrder.AddFirst(new LinkedListNode<string>(texPath));
        while (_pngMemory.Count > PngMemoryEntries && _pngOrder.Last != null)
        {
            _pngMemory.Remove(_pngOrder.Last.Value);
            _pngOrder.RemoveLast();
        }
    }

    private byte[]? DecodeTexturePng(string texPath)
    {
        var obj = _provider.SafeLoadPackageObject<UObject>(texPath.TrimStart('/'));
        if (obj is not UTexture texture) return null;

        var decoded = TextureDecoder.Decode(texture, ETexturePlatform.DesktopMobile);
        if (decoded == null) return null;

        using var bitmap = TextureEncoder.ToSkBitmap(decoded);
        if (bitmap == null) return null;

        SKBitmap? resized = null;
        try
        {
            var toEncode = bitmap;
            if (bitmap.Width > MaxTextureSize || bitmap.Height > MaxTextureSize)
            {
                var scale = (double)MaxTextureSize / Math.Max(bitmap.Width, bitmap.Height);
                var w = Math.Max(1, (int)(bitmap.Width * scale));
                var h = Math.Max(1, (int)(bitmap.Height * scale));
                resized = bitmap.Resize(new SKImageInfo(w, h, bitmap.ColorType, bitmap.AlphaType), SKFilterQuality.Medium);
                if (resized == null) return null;
                toEncode = resized;
            }

            using var img = SKImage.FromBitmap(toEncode);
            using var png = img.Encode(SKEncodedImageFormat.Png, 90);
            return png?.ToArray();
        }
        finally
        {
            resized?.Dispose();
        }
    }
}
