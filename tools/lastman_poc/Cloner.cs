using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;

internal static class Cloner
{
    private static readonly MethodInfo _memberwiseCloneMethod =
        typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static readonly FieldInfo _alreadySerializedField =
        typeof(UAssetAPI.ExportTypes.Export).GetField("alreadySerialized", BindingFlags.Instance | BindingFlags.NonPublic)!;

    internal sealed class Context
    {
        public readonly UAsset Src;
        public readonly UAsset Dst;
        public string? Failure { get; private set; }
        public bool SrcSameAsDst => _sameAsset;

        private readonly Dictionary<int, FPackageIndex> _exportMemo = new();
        private readonly Dictionary<int, FPackageIndex> _importMemo = new();
        private readonly bool _sameAsset;

        public Context(UAsset src, UAsset dst)
        {
            Src = src;
            Dst = dst;
            _sameAsset = ReferenceEquals(src, dst) || SamePath(src, dst);
        }

        public void Fail(string why) => Failure ??= why;

        public void MemoImport(int srcIdx, FPackageIndex dst) => _importMemo[srcIdx] = dst;
        public void MemoExport(int srcIdx, FPackageIndex dst) => _exportMemo[srcIdx] = dst;

        private static bool SamePath(UAsset a, UAsset b)
        {
            string pa = a.FilePath ?? "";
            string pb = b.FilePath ?? "";
            if (pa.Length == 0 || pb.Length == 0) return false;
            try { pa = Path.GetFullPath(pa); pb = Path.GetFullPath(pb); } catch { }
            return string.Equals(pa, pb, StringComparison.OrdinalIgnoreCase);
        }

        public FPackageIndex? Remap(FPackageIndex? pi)
        {
            if (pi == null) return null;
            if (_sameAsset) return pi;
            if (pi.Index == 0) return new FPackageIndex(0);
            if (pi.Index < 0)
            {
                var imp = EnsureImport(-pi.Index - 1);
                if (imp == null) Fail($"source import {-pi.Index} cannot be re-created");
                return imp ?? new FPackageIndex(0);
            }
            var exp = EnsureExport(pi.Index - 1);
            if (exp == null) Fail($"source export {pi.Index} cannot be cloned");
            return exp ?? new FPackageIndex(0);
        }

        public FPackageIndex? RemapTolerant(FPackageIndex? pi)
        {
            if (pi == null) return new FPackageIndex(0);
            if (_sameAsset) return pi;
            if (pi.Index == 0) return new FPackageIndex(0);
            if (pi.Index < 0) return EnsureImport(-pi.Index - 1) ?? new FPackageIndex(0);
            return EnsureExport(pi.Index - 1) ?? new FPackageIndex(0);
        }

        public FPackageIndex? EnsureImport(int srcIdx)
        {
            if (srcIdx < 0 || srcIdx >= Src.Imports.Count) return null;
            if (_sameAsset) return FPackageIndex.FromImport(srcIdx);
            if (_importMemo.TryGetValue(srcIdx, out var memo)) return memo;

            var imp = Src.Imports[srcIdx];
            string classPackage = imp.ClassPackage?.Value?.ToString() ?? "/Script/CoreUObject";
            string className = imp.ClassName?.Value?.ToString() ?? "Object";
            string objectName = imp.ObjectName?.Value?.ToString() ?? "";
            string packageName = imp.PackageName?.Value?.ToString() ?? "None";
            if (objectName.Length == 0) return null;

            FPackageIndex? outerIdx;
            if (imp.OuterIndex == null || imp.OuterIndex.Index == 0)
            {
                outerIdx = new FPackageIndex(0);
            }
            else if (imp.OuterIndex.Index < 0)
            {
                outerIdx = EnsureImport(-imp.OuterIndex.Index - 1);
                if (outerIdx == null) return null;
            }
            else
            {
                Fail($"import {srcIdx} has an export outer");
                return null;
            }
            FPackageIndex outer = outerIdx;

            for (int i = 0; i < Dst.Imports.Count; i++)
            {
                var d = Dst.Imports[i];
                if ((d.OuterIndex?.Index ?? int.MinValue) != outer.Index) continue;
                if (!string.Equals(d.ObjectName?.Value?.ToString(), objectName, StringComparison.Ordinal)) continue;
                if (!string.Equals(d.ClassName?.Value?.ToString(), className, StringComparison.Ordinal)) continue;
                if (!string.Equals(d.ClassPackage?.Value?.ToString(), classPackage, StringComparison.Ordinal)) continue;
                _importMemo[srcIdx] = FPackageIndex.FromImport(i);
                return _importMemo[srcIdx];
            }

            var add = new UAssetAPI.Import
            {
                ClassPackage = FName.FromString(Dst, classPackage),
                ClassName = FName.FromString(Dst, className),
                ObjectName = FName.FromString(Dst, objectName),
                OuterIndex = outer,
                PackageName = FName.FromString(Dst, packageName)
            };
            int idx = Dst.Imports.Count;
            Dst.Imports.Add(add);
            _importMemo[srcIdx] = FPackageIndex.FromImport(idx);
            return _importMemo[srcIdx];
        }

        public FPackageIndex? EnsureExport(int srcIdx)
        {
            if (srcIdx < 0 || srcIdx >= Src.Exports.Count) return null;
            if (_sameAsset) return FPackageIndex.FromExport(srcIdx);
            if (_exportMemo.TryGetValue(srcIdx, out var memo)) return memo;

            var exp = Src.Exports[srcIdx];
            if (exp is not NormalExport srcExp)
            {
                Fail($"export {srcIdx} ({exp.ObjectName?.Value}) has no property data");
                return null;
            }

            var clone = (NormalExport)CloneExportShell(srcExp, Dst, srcIdx)!;
            if (Failure != null) return null;

            _exportMemo[srcIdx] = FPackageIndex.FromExport(Dst.Exports.Count);
            Dst.Exports.Add(clone);

            var data = new List<PropertyData>(srcExp.Data?.Count ?? 0);
            if (srcExp.Data != null)
            {
                foreach (var p in srcExp.Data)
                {
                    if (CloneValue(p, this) is PropertyData cp) data.Add(cp);
                }
            }
            clone.Data = data;
            if (Failure != null)
            {
                Dst.Exports.RemoveAt(Dst.Exports.Count - 1);
                _exportMemo.Remove(srcIdx);
                return null;
            }
            return _exportMemo[srcIdx];
        }

        internal NormalExport? CloneExportShell(NormalExport srcExp, UAsset dst, int srcIdx)
        {
            var clone = (NormalExport)_memberwiseCloneMethod.Invoke(srcExp, null)!;
            clone.Asset = dst;
            clone.ClassIndex = Remap(srcExp.ClassIndex) ?? new FPackageIndex(0);
            clone.OuterIndex = Remap(srcExp.OuterIndex) ?? new FPackageIndex(0);
            clone.SuperIndex = RemapTolerant(srcExp.SuperIndex);
            clone.TemplateIndex = RemapTolerant(srcExp.TemplateIndex);
            if (Failure != null) return null;
            clone.ObjectName = FName.FromString(dst, UniqueObjectName(srcExp.ObjectName));
            clone.SerialSize = 0;
            clone.SerialOffset = 0;
            _alreadySerializedField.SetValue(clone, false);
            return clone;
        }

        private string UniqueObjectName(FName? srcName)
        {
            string baseName = srcName?.Value?.Value ?? "";
            if (baseName.Length == 0) baseName = "ExportedObject";
            else if (srcName!.Number > 0) baseName = $"{baseName}_{srcName.Number}";
            if (!IsNameTaken(Dst, baseName)) return baseName;
            for (int i = 0; i < 16; i++)
            {
                string candidate = baseName + "_Copy_" + Guid.NewGuid().ToString("N").ToUpperInvariant();
                if (!IsNameTaken(Dst, candidate)) return candidate;
            }
            return baseName + "_Copy_2";
        }

        private static bool IsNameTaken(UAsset asset, string name)
        {
            foreach (var e in asset.Exports)
                if (string.Equals(e.ObjectName?.Value?.ToString(), name, StringComparison.Ordinal))
                    return true;
            foreach (var im in asset.Imports)
                if (string.Equals(im.ObjectName?.Value?.ToString(), name, StringComparison.Ordinal))
                    return true;
            return false;
        }
    }

    internal static object MemberwiseCloneLocal(object src) => _memberwiseCloneMethod.Invoke(src, null)!;

    internal static void ResetAlreadySerialized(object exp) => _alreadySerializedField.SetValue(exp, false);

    internal static object? CloneValue(object? val, Context ctx)
    {
        if (val == null) return null;
        if (val is FName fn) return RetargetName(fn, ctx);
        if (val is FPackageIndex pi) return ctx.Remap(pi);
        if (val is PropertyData pd) return CloneProperty(pd, ctx);
        if (val is FPropertyTypeName tn) return CloneTypeName(tn, ctx);
        if (val is FPropertyTypeNameNode node)
            return new FPropertyTypeNameNode
            {
                Name = RetargetName(node.Name, ctx) ?? node.Name,
                InnerCount = node.InnerCount
            };
        if (val is TMap<PropertyData, PropertyData> map)
        {
            var tm = new TMap<PropertyData, PropertyData>();
            foreach (var kvp in map)
                tm.Add(CloneValue(kvp.Key, ctx) as PropertyData ?? kvp.Key,
                       CloneValue(kvp.Value, ctx) as PropertyData ?? kvp.Value);
            return tm;
        }
        if (val is List<PropertyData> list)
        {
            var nl = new List<PropertyData>(list.Count);
            foreach (var p in list)
                nl.Add(CloneValue(p, ctx) as PropertyData ?? p);
            return nl;
        }
        if (val is List<FPropertyTypeNameNode> nodes)
        {
            var nl = new List<FPropertyTypeNameNode>(nodes.Count);
            foreach (var n in nodes)
                nl.Add(new FPropertyTypeNameNode
                {
                    Name = RetargetName(n.Name, ctx) ?? n.Name,
                    InnerCount = n.InnerCount
                });
            return nl;
        }
        if (val is Array arr)
        {
            var na = Array.CreateInstance(arr.GetType().GetElementType()!, arr.Length);
            for (int i = 0; i < arr.Length; i++)
                na.SetValue(CloneValue(arr.GetValue(i), ctx), i);
            return na;
        }
        return val;
    }

    private static PropertyData CloneProperty(PropertyData src, Context ctx)
    {
        if (!ctx.SrcSameAsDst)
        {
            string? pt = src.PropertyType?.Value;
            if (!string.IsNullOrEmpty(pt)) FName.FromString(ctx.Dst, pt!);
            if (src is UnknownPropertyData unk)
            {
                string? ut = unk.SerializingPropertyType?.Value;
                if (!string.IsNullOrEmpty(ut)) FName.FromString(ctx.Dst, ut!);
            }
        }
        var clone = (PropertyData)_memberwiseCloneMethod.Invoke(src, null)!;
        WalkFields(clone, src.GetType(), ctx);
        return clone;
    }

    private static void WalkFields(object target, Type startType, Context ctx)
    {
        for (var t = startType; t != null && t != typeof(object); t = t.BaseType)
        {
            foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (f.IsStatic) continue;
                object? val;
                try { val = f.GetValue(target); } catch { continue; }
                var nv = CloneValue(val, ctx);
                if (ReferenceEquals(val, nv)) continue;
                f.SetValue(target, nv);
            }
        }
    }

    private static FName? RetargetName(FName? fn, Context ctx)
    {
        if (fn == null) return null;
        string? s = fn.Value?.Value;
        if (s == null) return fn;
        if (ctx.SrcSameAsDst) return fn;
        return new FName(ctx.Dst, s, fn.Number);
    }

    private static FPropertyTypeName CloneTypeName(FPropertyTypeName tn, Context ctx)
    {
        var nodes = new List<FPropertyTypeNameNode>(tn.Nodes?.Count ?? 0);
        if (tn.Nodes != null)
        {
            foreach (var n in tn.Nodes)
                nodes.Add(new FPropertyTypeNameNode
                {
                    Name = RetargetName(n.Name, ctx) ?? n.Name,
                    InnerCount = n.InnerCount
                });
        }
        return new FPropertyTypeName(nodes, tn.ShouldSerializeNodes);
    }
}
