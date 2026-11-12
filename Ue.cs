using System.Numerics;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Assets.Objects.Properties;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.Core.i18N;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.Objects.Engine;
using CUE4Parse.UE4.Assets;

// Helpers for reading cooked UE4 objects with archetype (Template) fallback.
static class Ue
{
    public static UObject? Tmpl(UObject o)
    {
        try { return o.Template?.Load(); } catch { return null; }
    }

    public static FPropertyTag? FindTag(UObject? o, string name)
    {
        for (int guard = 0; o != null && guard < 16; guard++, o = Tmpl(o))
        {
            foreach (var t in o.Properties)
                if (t.Name.Text == name) return t;
        }
        return null;
    }

    public static object? Val(UObject? o, string name) => FindTag(o, name)?.Tag?.GenericValue;

    public static UObject? Obj(UObject? o, string name)
    {
        var v = Val(o, name);
        return LoadRef(v);
    }

    public static UObject? LoadRef(object? v)
    {
        try
        {
            return v switch
            {
                FPackageIndex pi when !pi.IsNull => pi.Load(),
                UObject uo => uo,
                _ => null
            };
        }
        catch { return null; }
    }

    public static FVector Vec(UObject? o, string name, FVector def)
    {
        var v = Val(o, name);
        if (v is FScriptStruct ss && ss.StructType is FVector fv) return fv;
        return def;
    }

    public static FRotator Rot(UObject? o, string name)
    {
        var v = Val(o, name);
        if (v is FScriptStruct ss && ss.StructType is FRotator fr) return fr;
        return new FRotator(0, 0, 0);
    }

    public static bool Bool(UObject? o, string name, bool def)
    {
        var v = Val(o, name);
        return v is bool b ? b : def;
    }

    // UE row-vector matrix: Scale * Rotation * Translation (FScaleRotationTranslationMatrix)
    public static Matrix4x4 Srt(FVector s, FRotator r, FVector t)
    {
        double p = r.Pitch * Math.PI / 180, y = r.Yaw * Math.PI / 180, rl = r.Roll * Math.PI / 180;
        float SP = (float)Math.Sin(p), CP = (float)Math.Cos(p);
        float SY = (float)Math.Sin(y), CY = (float)Math.Cos(y);
        float SR = (float)Math.Sin(rl), CR = (float)Math.Cos(rl);
        var m = new Matrix4x4(
            CP * CY * s.X, CP * SY * s.X, SP * s.X, 0,
            (SR * SP * CY - CR * SY) * s.Y, (SR * SP * SY + CR * CY) * s.Y, (-SR * CP) * s.Y, 0,
            (-(CR * SP * CY + SR * SY)) * s.Z, (CY * SR - CR * SP * SY) * s.Z, (CR * CP) * s.Z, 0,
            t.X, t.Y, t.Z, 1);
        return m;
    }

    public static Matrix4x4 Relative(UObject comp)
    {
        var loc = Vec(comp, "RelativeLocation", new FVector(0, 0, 0));
        var rot = Rot(comp, "RelativeRotation");
        var scl = Vec(comp, "RelativeScale3D", new FVector(1, 1, 1));
        return Srt(scl, rot, loc);
    }

    // World matrix of a scene component (row-vector convention: v_world = v_local * M)
    public static Matrix4x4 World(UObject comp, Dictionary<UObject, Matrix4x4> cache, Matrix4x4 levelXf, int depth = 0)
    {
        if (cache.TryGetValue(comp, out var m)) return m;
        var rel = Relative(comp);
        var parent = depth < 32 ? Obj(comp, "AttachParent") : null;
        Matrix4x4 w;
        if (parent != null && parent != comp) w = rel * World(parent, cache, levelXf, depth + 1);
        else w = rel * levelXf;
        cache[comp] = w;
        return w;
    }

    public static List<string> Ancestry(UStruct? cls)
    {
        var res = new List<string>();
        for (int g = 0; cls != null && g < 32; g++)
        {
            res.Add(cls.Name);
            try { cls = cls.SuperStruct?.Load<UStruct>(); } catch { break; }
        }
        return res;
    }

    public static Dictionary<string, object?> MergedProps(UObject? o, HashSet<string>? skip = null)
    {
        var res = new Dictionary<string, object?>();
        for (int guard = 0; o != null && guard < 16; guard++, o = Tmpl(o))
            foreach (var t in o.Properties)
            {
                var n = t.Name.Text;
                if (res.ContainsKey(n) || (skip != null && skip.Contains(n))) continue;
                var gv = t.Tag?.GenericValue;
                if (gv is FPackageIndex pi && !pi.IsNull)
                {
                    try { var r = pi.ResolvedObject; var path = r?.GetPathName() ?? ""; if (path.Contains("_GEN_VARIABLE") || path.Contains(":" + o.Name + ".") || path.Contains("." + o.Name + ".")) continue; } catch { }
                }
                var sv = Simplify(gv);
                if (sv != null) res[n] = sv;
            }
        return res;
    }

    public static string? Text(object? v) => v is FText t ? t.Text : null;

    // Generic, compact JSON-able conversion of a property value. Object refs become path strings.
    public static object? Simplify(object? v, int depth = 0)
    {
        if (depth > 6) return null;
        switch (v)
        {
            case null: return null;
            case bool or int or uint or long or ulong or float or double or byte or short or ushort or sbyte or string: return v;
            case FName n: return n.Text;
            case FText t: return t.Text;
            case FPackageIndex pi:
                if (pi.IsNull) return null;
                try { var r = pi.ResolvedObject; return r == null ? null : r.GetPathName(); } catch { return pi.ToString(); }
            case FSoftObjectPath sp: return sp.AssetPathName.Text;
            case FScriptStruct ss:
                return ss.StructType switch
                {
                    FVector fv => new[] { fv.X, fv.Y, fv.Z },
                    FRotator fr => new[] { fr.Pitch, fr.Yaw, fr.Roll },
                    FLinearColor lc => new[] { lc.R, lc.G, lc.B, lc.A },
                    FColor c => new[] { c.R, c.G, c.B, c.A },
                    FTransform ft => new Dictionary<string, object?> { ["t"] = new[] { ft.Translation.X, ft.Translation.Y, ft.Translation.Z }, ["q"] = new[] { ft.Rotation.X, ft.Rotation.Y, ft.Rotation.Z, ft.Rotation.W }, ["s"] = new[] { ft.Scale3D.X, ft.Scale3D.Y, ft.Scale3D.Z } },
                    FQuat q => new[] { q.X, q.Y, q.Z, q.W },
                    FStructFallback fb => StructDict(fb, depth),
                    _ => ss.StructType?.ToString()
                };
            case FScriptInterface si: return Simplify(si.Object, depth + 1);
            case UScriptSet set: return set.Properties.Select(p => Simplify(p.GenericValue, depth + 1)).ToList();
            case UScriptArray arr: return arr.Properties.Select(p => Simplify(p.GenericValue, depth + 1)).ToList();
            case UScriptMap map:
                return map.Properties.Select(kv => new object?[] { Simplify(kv.Key?.GenericValue, depth + 1), Simplify(kv.Value?.GenericValue, depth + 1) }).ToList();
            case FMulticastScriptDelegate: return null;
            case FScriptDelegate: return null;
            case UObject uo: return uo.GetPathName();
            default:
                var s = v.ToString();
                return s;
        }
    }

    static Dictionary<string, object?> StructDict(FStructFallback fb, int depth)
    {
        var d = new Dictionary<string, object?>();
        foreach (var p in fb.Properties) d[CleanName(p.Name.Text)] = Simplify(p.Tag?.GenericValue, depth + 1);
        return d;
    }

    // BP struct members are named Foo_12_ABCDEF...; strip the suffix.
    public static string CleanName(string n)
    {
        var m = System.Text.RegularExpressions.Regex.Match(n, @"^(.*?)_\d+_[0-9A-F]{32}$");
        return m.Success ? m.Groups[1].Value : n;
    }
}
