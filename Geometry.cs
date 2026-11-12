using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse_Conversion.Textures;

// Average display color per material, derived from its base color texture and tint parameters.
class MaterialColors
{
    // MaskTex: for cut-out materials whose color texture has no alpha, a separate mask whose red channel is the alpha.
    // FillTex: drawn under Tex's see-through pixels to make an opaque texture (see the trim layer note in Compute).
    public record MatInfo(float R, float G, float B, float A, bool Skip, string Key,
        UTexture2D? Tex = null, Vector3? Tint = null, bool Masked = false, float Tile = 1f, UTexture2D? MaskTex = null, bool Unlit = false,
        UTexture2D? FillTex = null);

    readonly Dictionary<string, MatInfo> cache = new();
    readonly Dictionary<string, (Vector3? Avg, float Transparent)> texCache = new();
    // One line per material: which texture parameter won and why (out/materials_log.tsv, written by the geometry step).
    public readonly List<string> Log = new();
    static readonly Regex BaseTexName = new(@"base|albedo|diffuse|color|colour|^tex|texture|main|_d$|_bc$|_c$|_alb|_col", RegexOptions.IgnoreCase);
    // Texture parameter names that are the surface color, and ones that are a secondary layer blended over it.
    static readonly Regex MainParam = new(@"main|base|albedo|diffuse|colou?r", RegexOptions.IgnoreCase);
    static readonly Regex SecondaryParam = new(@"alternate|\balt|blend|detail|second|overlay|layer ?2|top|damage|dirt|moss|snow|wet|puddle", RegexOptions.IgnoreCase);
    static readonly Regex NotColorTex = new(@"normal|_n$|_nrm|rough|mask|orm|_arm|height|emiss|_e$|metal|_ao|_r$|_m$|noise|spec|opacity|alpha|disp|bump|flow|_h$|_mra|_rma|_rmo|_orm|lut|cloud|gradient|ramp|bayer|dither|bubb|_fx|fx_|glint|sparkle", RegexOptions.IgnoreCase);
    // "RustedSword_MI" -> "rustedsword": textures named after their material are its own color texture.
    static string Stem(string matName) => Regex.Replace(matName, @"(_?(mi|ma|mat|material|inst|mib|master))+(_\d+)?$", "", RegexOptions.IgnoreCase).ToLowerInvariant();
    static bool NamedAfter(UObject tex, string stem) => stem.Length >= 4 && tex.Name.ToLowerInvariant().StartsWith(stem);
    static readonly Regex TintName = new(@"color|colour|tint|albedo|base", RegexOptions.IgnoreCase);

    public MatInfo Get(UObject? mat)
    {
        if (mat == null) return new(0.6f, 0.6f, 0.6f, 1, false, "none");
        var key = mat.GetPathName();
        if (cache.TryGetValue(key, out var mi)) return mi;
        mi = Compute(mat, key);
        cache[key] = mi;
        return mi;
    }

    // Blend mode: the first instance in the parent chain that overrides it, else the base material's.
    // Shading model (MSM_Unlit for the ocean floor): an instance override, else the base material's.
    static string ShadingOf(UObject mat)
    {
        UObject? cur = mat;
        for (int g = 0; cur != null && g < 12; g++)
        {
            if (Ue.Val(cur, "BasePropertyOverrides") is FScriptStruct ss && ss.StructType is FStructFallback fb
                && fb.Properties.FirstOrDefault(p => p.Name.Text == "ShadingModel")?.Tag?.GenericValue is { } sm)
                return sm.ToString() ?? "";
            if (cur is UMaterial um) return Ue.Val(um, "ShadingModel")?.ToString() ?? "";
            cur = Ue.Obj(cur, "Parent");
        }
        return "";
    }

    static string BlendOf(UObject mat)
    {
        UObject? cur = mat;
        for (int g = 0; cur != null && g < 12; g++)
        {
            if (Ue.Val(cur, "BasePropertyOverrides") is FScriptStruct ss && ss.StructType is FStructFallback fb)
            {
                bool over = fb.Properties.Any(p => p.Name.Text == "bOverride_BlendMode" && p.Tag?.GenericValue is true);
                var bm = fb.Properties.FirstOrDefault(p => p.Name.Text == "BlendMode")?.Tag?.GenericValue;
                if (bm != null && (over || cur is UMaterialInstance)) return bm.ToString() ?? "";
            }
            if (cur is UMaterial um) return Ue.Val(um, "BlendMode")?.ToString() ?? "";
            cur = Ue.Obj(cur, "Parent");
        }
        return "";
    }

    MatInfo Compute(UObject mat, string key)
    {
        Vector3? texColor = null; Vector3? tint = null, onlyColor = null; UTexture2D? texObjBest = null; UTexture2D? maskTex = null, fillTex = null; float? tile = null;
        string? blend = BlendOf(mat);
        bool unlit = ShadingOf(mat).Contains("Unlit");
        bool maskedMat = blend.Contains("Masked");
        string why = "";
        UObject? cur = mat;
        var lname = mat.Name.ToLowerInvariant();
        for (int g = 0; cur != null && g < 12; g++)
        {
            if (cur is UMaterialInstance)
            {
                if (texColor == null && Ue.Val(cur, "TextureParameterValues") is UScriptArray tarr)
                {
                    var cands = new List<(int score, UTexture2D tex, string pname)>();
                    var masks = new List<UTexture2D>();
                    // A child material that overrides a single texture of its parent (a painting's picture, say): that
                    // texture is what makes it different, so it wins unless it is just a weathering layer.
                    bool singleOverride = g == 0 && tarr.Properties.Count == 1 && Ue.Obj(cur, "Parent") is UMaterialInstance;
                    foreach (var p in tarr.Properties)
                    {
                        if (p.GenericValue is not FScriptStruct s || s.StructType is not FStructFallback f) continue;
                        string pname = ParamName(f);
                        var texObj = Ue.LoadRef(f.Properties.FirstOrDefault(x => x.Name.Text == "ParameterValue")?.Tag?.GenericValue) as UTexture2D;
                        if (texObj == null || IsEngineTex(texObj)) continue;
                        if (MaskParam.IsMatch(pname) && !Regex.IsMatch(texObj.Name, "noise|blend", RegexOptions.IgnoreCase)) masks.Add(texObj);
                        int score = 0;
                        if (singleOverride && !Regex.IsMatch(pname, "damage|dirt|moss|snow|wet|puddle", RegexOptions.IgnoreCase)) score += 3;
                        if (MainParam.IsMatch(pname)) score += 3;
                        else if (BaseTexName.IsMatch(pname)) score += 1;
                        if (SecondaryParam.IsMatch(pname)) score -= 2;
                        if (NotColorTex.IsMatch(pname)) score -= 5;
                        if (NotColorTex.IsMatch(texObj.Name)) score -= 3;
                        if (BaseTexName.IsMatch(texObj.Name)) score += 1;
                        if (NamedAfter(texObj, Stem(mat.Name))) score += 2;
                        // A cut-out (masked) material takes its shape from the color texture's alpha: prefer one that has it.
                        if (maskedMat && score >= 0 && TexStats(texObj).Transparent > 0.005f) score += 4;
                        cands.Add((score, texObj, pname));
                    }
                    var best = cands.OrderByDescending(c => c.score).FirstOrDefault();
                    if (best.tex != null && best.score >= 0) { texColor = TexStats(best.tex).Avg; texObjBest = best.tex; }
                    // Layered wall materials (Env_BlendAltTrim: vertex color R paints the trim layer) take the cut-out from
                    // the trim texture only where R is painted; elsewhere the main layer shows. The map has one texture per
                    // material, so the trim's see-through pixels are filled with the main texture and the surface drawn
                    // opaque. Otherwise the gaps show whatever is behind (the Tower of Knowledge's wood interior).
                    if (maskedMat && best.tex != null && Regex.IsMatch(best.pname, "trim", RegexOptions.IgnoreCase))
                        fillTex = cands.Where(c => c.tex != best.tex && MainParam.IsMatch(c.pname) && c.score >= 0 && TexStats(c.tex).Transparent < 0.005f)
                            .OrderByDescending(c => c.score).Select(c => c.tex).FirstOrDefault();
                    why = string.Join(", ", cands.Select(c => $"{c.pname}={c.tex.Name}:{c.score}"));
                    // Cut-out shape from a separate mask (cobwebs): only when the color texture has no alpha of its own,
                    // and the mask really has both see-through and solid parts.
                    if (maskedMat && texObjBest != null && TexStats(texObjBest).Transparent < 0.005f)
                        maskTex = masks.FirstOrDefault(m => MaskCoverage(m) is > 0.02f and < 0.98f);
                }
                if (tint == null && Ue.Val(cur, "VectorParameterValues") is UScriptArray varr)
                {
                    foreach (var p in varr.Properties)
                    {
                        if (p.GenericValue is not FScriptStruct s || s.StructType is not FStructFallback f) continue;
                        string pname = ParamName(f);
                        var v = f.Properties.FirstOrDefault(x => x.Name.Text == "ParameterValue")?.Tag?.GenericValue;
                        if (v is not FScriptStruct vs || vs.StructType is not FLinearColor lc) continue;
                        // A material whose only color parameter is something else (the ocean floor's "skyfogcolor") is that color.
                        if (varr.Properties.Count == 1) onlyColor ??= new Vector3(ToSrgb(lc.R), ToSrgb(lc.G), ToSrgb(lc.B));
                        if (!TintName.IsMatch(pname) || Regex.IsMatch(pname, "emis|fog|glow|light|fresnel|rim|edge|sss|subsurface|spec", RegexOptions.IgnoreCase)) continue;
                        tint = new Vector3(ToSrgb(lc.R), ToSrgb(lc.G), ToSrgb(lc.B));
                        break;
                    }
                }
                if (tile == null && Ue.Val(cur, "ScalarParameterValues") is UScriptArray sarr)
                {
                    foreach (var p in sarr.Properties)
                    {
                        if (p.GenericValue is not FScriptStruct s || s.StructType is not FStructFallback f) continue;
                        string pname = ParamName(f);
                        if (!Regex.IsMatch(pname, @"^(uv ?)?til(e|ing)|^uv ?scale|^texture ?scale|^tex ?scale|^base ?tiling|^diffuse ?tiling", RegexOptions.IgnoreCase)) continue;
                        if (f.Properties.FirstOrDefault(x => x.Name.Text == "ParameterValue")?.Tag?.GenericValue is float fv && fv > 0.01f && fv < 100f) { tile = fv; break; }
                    }
                }
                cur = Ue.Obj(cur, "Parent");
                continue;
            }
            if (cur is UMaterial um)
            {
                if (texColor == null)
                {
                    try
                    {
                        var texes = um.ReferencedTextures?.OfType<UTexture2D>().ToList() ?? new();
                        var best = texes.Where(t => !IsEngineTex(t) && !NotColorTex.IsMatch(t.Name))
                            .OrderByDescending(t => (BaseTexName.IsMatch(t.Name) ? 1 : 0) + (NamedAfter(t, Stem(mat.Name)) ? 5 : 0)
                                + (maskedMat && TexStats(t).Transparent > 0.005f ? 4 : 0)).FirstOrDefault();
                        if (best != null) { texColor = TexStats(best).Avg; texObjBest = best; }
                        why = "material textures: " + string.Join(", ", texes.Select(t => t.Name));
                    }
                    catch { }
                }
            }
            break;
        }
        Log.Add(string.Join('\t', key, blend, texObjBest?.Name ?? "-", texObjBest != null ? TexStats(texObjBest).Transparent.ToString("0.000") : "-",
            (maskTex != null ? $"[mask {maskTex.Name}] " : "") + (fillTex != null ? $"[opaque, filled with {fillTex.Name}] " : "") + why));
        bool additive = blend.Contains("Additive") || blend.Contains("Modulate");
        bool translucent = blend.Contains("Translucent");
        if (lname == "worldgridmaterial" || lname == "defaultmaterial") return new(0.5f, 0.5f, 0.5f, 1, true, key);
        bool fx = Regex.IsMatch(lname, @"fog|cloud|godray|lightbeam|light_beam|beam|glow|flare|shadowonly|invisible|caustic|decal|volumetric|_fx|fx_|smoke|dust|particle");
        // A near-black "color" parameter on a textured material is a fade/dissolve/effect color, not an albedo tint.
        if (texColor != null && tint != null && tint.Value.X + tint.Value.Y + tint.Value.Z < 0.15f) tint = null;
        Vector3 col = texColor ?? tint ?? onlyColor ?? HashColor(key);
        if (texColor != null && tint != null) col *= tint.Value;
        float a = translucent ? 0.45f : 1f;
        bool skip = additive || (translucent && fx) || (fx && Regex.IsMatch(lname, "fog|cloud|godray|beam|flare|volumetric|shadowonly|invisible"));
        // Water surfaces are see-through; the solid floor under the sea (bottofocean) is not.
        if (Regex.IsMatch(lname, "water|ocean|sea_|wave") && !lname.Contains("bottofocean")) { a = Math.Min(a, 0.6f); }
        bool masked = blend.Contains("Masked") && fillTex == null;
        return new(col.X, col.Y, col.Z, a, skip, key, texColor != null ? texObjBest : null, tint, masked, tile ?? 1f, texColor != null ? maskTex : null, unlit,
            texColor != null ? fillTex : null);
    }

    static readonly Regex MaskParam = new(@"^mask$|opacity|alpha|cutout|_m$|^m$", RegexOptions.IgnoreCase);
    readonly Dictionary<string, float> maskCache = new();
    // Fraction of a mask texture that is dark (red channel < 0.5), i.e. see-through when used as alpha.
    float MaskCoverage(UTexture2D tex)
    {
        var key = tex.GetPathName();
        if (maskCache.TryGetValue(key, out var v)) return v;
        v = 0;
        try
        {
            using var bmp = tex.Decode(64);
            if (bmp != null)
            {
                int dark = 0;
                for (int y = 0; y < bmp.Height; y++)
                    for (int x = 0; x < bmp.Width; x++)
                        if (bmp.GetPixel(x, y).Red < 128) dark++;
                v = (float)dark / (bmp.Width * bmp.Height);
            }
        }
        catch { }
        maskCache[key] = v;
        return v;
    }

    // Engine placeholder textures (BlendFunc_DefBase, DefaultDiffuse, ...) come in through material functions' default
    // inputs. Their names match "base"/"diffuse", so without this they beat the game's real textures. Post-process
    // helper textures (VFX/Post: dither patterns) are never a surface color either.
    static bool IsEngineTex(UObject t)
    {
        var p = t.GetPathName();
        return p.StartsWith("Engine/", StringComparison.OrdinalIgnoreCase) || p.StartsWith("/Engine/", StringComparison.OrdinalIgnoreCase)
            || p.Contains("/VFX/Post/", StringComparison.OrdinalIgnoreCase);
    }

    static string ParamName(FStructFallback f)
    {
        var pi = f.Properties.FirstOrDefault(x => x.Name.Text == "ParameterInfo")?.Tag?.GenericValue;
        if (pi is FScriptStruct ps && ps.StructType is FStructFallback pf)
            return pf.Properties.FirstOrDefault(x => x.Name.Text == "Name")?.Tag?.GenericValue?.ToString() ?? "";
        return "";
    }

    static float ToSrgb(float l) => l <= 0.0031308f ? l * 12.92f : 1.055f * MathF.Pow(Math.Clamp(l, 0, 1), 1 / 2.4f) - 0.055f;

    static Vector3 HashColor(string s)
    {
        uint h = 2166136261; foreach (var ch in s) h = (h ^ ch) * 16777619;
        float v = 0.45f + (h % 100) / 400f;
        return new Vector3(v, v * 0.98f, v * 0.95f);
    }

    // Average color (alpha-weighted) and the fraction of pixels that are transparent (alpha < 0.5).
    (Vector3? Avg, float Transparent) TexStats(UTexture2D tex)
    {
        var key = tex.GetPathName();
        if (texCache.TryGetValue(key, out var c)) return c;
        (Vector3? Avg, float Transparent) res = (null, 0);
        try
        {
            using var bmp = tex.Decode(64);
            if (bmp != null)
            {
                double r = 0, g = 0, b = 0, w = 0; int clear = 0;
                for (int y = 0; y < bmp.Height; y++)
                    for (int x = 0; x < bmp.Width; x++)
                    {
                        var px = bmp.GetPixel(x, y);
                        double a = px.Alpha / 255.0;
                        if (a < 0.5) clear++;
                        if (a < 0.3) continue;
                        r += px.Red * a; g += px.Green * a; b += px.Blue * a; w += a;
                    }
                res.Transparent = (float)clear / (bmp.Width * bmp.Height);
                if (w > 0) res.Avg = new Vector3((float)(r / w / 255), (float)(g / w / 255), (float)(b / w / 255));
            }
        }
        catch { }
        texCache[key] = res;
        return res;
    }
}

// Mesh data extracted once per UStaticMesh (positions already in glTF axes; indices with reversed winding).
class MeshData
{
    public required string Path;
    public float[] Pos = Array.Empty<float>();
    public float[]? Uv;
    public List<(uint[] Idx, int MatSlot)> Sections = new();
    public List<UObject?> Mats = new();
    public long Tris;
}

// Some levels' backdrop meshes reach far outside their own space. In the game those levels are never loaded together,
// but the map shows them all at once, so a backdrop can cut through another area. KeepBoxes lists (level, mesh) pairs
// whose triangles are clipped to a box (UE world X/Y; height unbounded), cutting triangles exactly at the box faces.
static class MeshClip
{
    public static readonly Dictionary<(string Level, string Mesh), (Vector2 Lo, Vector2 Hi)> KeepBoxes = new()
    {
        // Final zone cliff shell and water plane: their north arm runs through Record of Sin's lower floor and along
        // Autumn's east edge, their south-west arm through the Castle's south. Keep the part around the Final zone itself.
        [("800_FinalZone", "FinalZone_SM")] = (new Vector2(-32000, -1e9f), new Vector2(1e9f, -2000)),
        [("800_FinalZone", "FZ_Water_SM")] = (new Vector2(-32000, -1e9f), new Vector2(1e9f, -2000)),
        // Not FZ_TheEnd_SM: it is the Final zone's domed bowl, which really does overlap Autumn's south edge and the
        // Castle exterior; clipping it cuts the dome in half.
    };

    record struct V(Vector3 P, Vector2 Uv);

    // Returns world-space geometry (glTF axes) to be added with an identity transform, or null if nothing is left.
    public static MeshData? ToBox(MeshData src, Matrix4x4 worldUe, Vector2 lo, Vector2 hi)
    {
        int n = src.Pos.Length / 3;
        var w = new Vector3[n];
        for (int i = 0; i < n; i++)
            w[i] = Vector3.Transform(new Vector3(src.Pos[i * 3], src.Pos[i * 3 + 2], src.Pos[i * 3 + 1]), worldUe); // UE world
        var pos = new List<float>(); var uv = new List<float>();
        var dst = new MeshData { Path = src.Path + "#clip", Mats = src.Mats };
        // keep side of each plane: (axis, value, keep >= value?)
        var planes = new (int Ax, float Val, bool Ge)[] { (0, lo.X, true), (0, hi.X, false), (1, lo.Y, true), (1, hi.Y, false) };
        foreach (var (idx, slot) in src.Sections)
        {
            var outIdx = new List<uint>();
            for (int t = 0; t + 2 < idx.Length; t += 3)
            {
                var poly = new List<V>(6);
                for (int k = 0; k < 3; k++)
                {
                    int vi = (int)idx[t + k];
                    poly.Add(new V(w[vi], src.Uv != null ? new Vector2(src.Uv[vi * 2], src.Uv[vi * 2 + 1]) : Vector2.Zero));
                }
                foreach (var pl in planes)
                {
                    if (poly.Count == 0) break;
                    float D(V v) { float d = (pl.Ax == 0 ? v.P.X : v.P.Y) - pl.Val; return pl.Ge ? d : -d; } // >= 0: keep
                    var next = new List<V>(poly.Count + 2);
                    for (int a = 0; a < poly.Count; a++)
                    {
                        var A = poly[a]; var B = poly[(a + 1) % poly.Count];
                        float da = D(A), db = D(B);
                        if (da >= 0) next.Add(A);
                        if ((da >= 0) != (db >= 0))
                        {
                            float s = da / (da - db);
                            next.Add(new V(Vector3.Lerp(A.P, B.P, s), Vector2.Lerp(A.Uv, B.Uv, s)));
                        }
                    }
                    poly = next;
                }
                if (poly.Count < 3) continue;
                uint baseI = (uint)(pos.Count / 3);
                foreach (var v in poly) { pos.Add(v.P.X); pos.Add(v.P.Z); pos.Add(v.P.Y); uv.Add(v.Uv.X); uv.Add(v.Uv.Y); }
                for (int k = 1; k + 1 < poly.Count; k++) { outIdx.Add(baseI); outIdx.Add(baseI + (uint)k); outIdx.Add(baseI + (uint)k + 1); }
            }
            if (outIdx.Count > 0) { dst.Sections.Add((outIdx.ToArray(), slot)); dst.Tris += outIdx.Count / 3; }
        }
        if (dst.Tris == 0) return null;
        dst.Pos = pos.ToArray();
        dst.Uv = src.Uv != null ? uv.ToArray() : null;
        return dst;
    }
}

static class SplineDeform
{
    static Vector3 Hermite(Vector3 p0, Vector3 t0, Vector3 p1, Vector3 t1, float a)
    {
        float a2 = a * a, a3 = a2 * a;
        return (2 * a3 - 3 * a2 + 1) * p0 + (a3 - 2 * a2 + a) * t0 + (-2 * a3 + 3 * a2) * p1 + (a3 - a2) * t1;
    }
    static Vector3 HermiteD(Vector3 p0, Vector3 t0, Vector3 p1, Vector3 t1, float a)
    {
        float a2 = a * a;
        return (6 * a2 - 6 * a) * p0 + (3 * a2 - 4 * a + 1) * t0 + (-6 * a2 + 6 * a) * p1 + (3 * a2 - 2 * a) * t1;
    }

    public static MeshData Apply(MeshData src, SplineInfo s)
    {
        var dst = new MeshData { Path = src.Path + "#spline" + Guid.NewGuid().ToString("N")[..8], Sections = src.Sections, Mats = src.Mats, Tris = src.Tris, Uv = src.Uv };
        int n = src.Pos.Length / 3;
        // UE-local vertex: (X, Y, Z) = (pos[0], pos[2], pos[1])
        float GetAxis(int i, int ax) => ax == 0 ? src.Pos[i * 3] : ax == 1 ? src.Pos[i * 3 + 2] : src.Pos[i * 3 + 1];
        float mn = float.MaxValue, mx = float.MinValue;
        for (int i = 0; i < n; i++) { var f = GetAxis(i, s.ForwardAxis); mn = Math.Min(mn, f); mx = Math.Max(mx, f); }
        float len = Math.Max(1e-3f, mx - mn);
        int ax1 = s.ForwardAxis == 0 ? 1 : 0, ax2 = s.ForwardAxis == 2 ? 1 : 2;
        dst.Pos = new float[src.Pos.Length];
        for (int i = 0; i < n; i++)
        {
            float a = Math.Clamp((GetAxis(i, s.ForwardAxis) - mn) / len, 0, 1);
            float ai = s.Smooth ? a * a * (3 - 2 * a) : a;
            var p = Hermite(s.StartPos, s.StartTan, s.EndPos, s.EndTan, a);
            var dir = HermiteD(s.StartPos, s.StartTan, s.EndPos, s.EndTan, a);
            if (dir.LengthSquared() < 1e-6f) dir = s.EndPos - s.StartPos;
            if (dir.LengthSquared() < 1e-6f) dir = Vector3.UnitX;
            dir = Vector3.Normalize(dir);
            var right = Vector3.Cross(s.UpDir, dir);
            if (right.LengthSquared() < 1e-6f) right = Vector3.Cross(Vector3.UnitY, dir);
            right = Vector3.Normalize(right);
            var up = Vector3.Normalize(Vector3.Cross(dir, right));
            float roll = s.StartRoll + (s.EndRoll - s.StartRoll) * ai;
            float cr = MathF.Cos(roll), sr = MathF.Sin(roll);
            var r2 = right * cr + up * sr; var u2 = -right * sr + up * cr;
            var scale = Vector2.Lerp(s.StartScale, s.EndScale, ai);
            var off = Vector2.Lerp(s.StartOffset, s.EndOffset, ai);
            float c1 = GetAxis(i, ax1) * scale.X + off.X, c2 = GetAxis(i, ax2) * scale.Y + off.Y;
            var w = p + r2 * c1 + u2 * c2;
            dst.Pos[i * 3] = w.X; dst.Pos[i * 3 + 1] = w.Z; dst.Pos[i * 3 + 2] = w.Y;
        }
        return dst;
    }
}

class MeshCache
{
    readonly Dictionary<string, MeshData?> cache = new();

    public MeshData? Get(UStaticMesh sm)
    {
        var key = sm.GetPathName();
        if (cache.TryGetValue(key, out var md)) return md;
        md = Build(sm, key);
        cache[key] = md;
        return md;
    }

    static MeshData? Build(UStaticMesh sm, string key)
    {
        var lods = sm.RenderData?.LODs;
        if (lods == null || lods.Length == 0) return null;
        var lod = lods.FirstOrDefault(l => !l.SkipLod && l.PositionVertexBuffer != null && l.IndexBuffer != null);
        if (lod == null) return null;
        var verts = lod.PositionVertexBuffer!.Verts;
        var md = new MeshData { Path = key };
        md.Pos = new float[verts.Length * 3];
        for (int i = 0; i < verts.Length; i++)
        {
            md.Pos[i * 3] = verts[i].X; md.Pos[i * 3 + 1] = verts[i].Z; md.Pos[i * 3 + 2] = verts[i].Y;
        }
        try
        {
            var uvs = lod.VertexBuffer?.UV;
            if (uvs != null && uvs.Length == verts.Length && lod.VertexBuffer!.NumTexCoords > 0)
            {
                md.Uv = new float[verts.Length * 2];
                for (int i = 0; i < verts.Length; i++) { var u = uvs[i].UV[0]; md.Uv[i * 2] = u.U; md.Uv[i * 2 + 1] = u.V; }
            }
        }
        catch { md.Uv = null; }
        var ib = lod.IndexBuffer!;
        foreach (var s in lod.Sections)
        {
            if (s.NumTriangles <= 0) continue;
            var idx = new uint[s.NumTriangles * 3];
            for (int t = 0; t < s.NumTriangles; t++)
            {
                int b = s.FirstIndex + t * 3;
                idx[t * 3] = (uint)ib[b]; idx[t * 3 + 1] = (uint)ib[b + 2]; idx[t * 3 + 2] = (uint)ib[b + 1];
            }
            md.Sections.Add((idx, s.MaterialIndex));
            md.Tris += s.NumTriangles;
        }
        try
        {
            foreach (var m in sm.StaticMaterials ?? Array.Empty<FStaticMaterial>())
                md.Mats.Add(m.MaterialInterface?.Load());
        }
        catch { }
        if (md.Mats.Count == 0 && sm.Materials != null)
            foreach (var m in sm.Materials) { try { md.Mats.Add(m?.Load()); } catch { md.Mats.Add(null); } }
        return md;
    }
}

// Exports each base color texture once, shared by every level.
class TextureStore
{
    readonly string dir;
    readonly Dictionary<string, string?> files = new();
    public TextureStore(string dir) { this.dir = dir; Directory.CreateDirectory(dir); }
    public int Count => files.Values.Count(v => v != null);

    // mask: optional separate cut-out mask; its red channel becomes the alpha (the texture is then always a PNG).
    // fill: optional texture drawn under tex's see-through pixels; the result is opaque.
    public string? Get(UTexture2D tex, bool alpha, UTexture2D? mask = null, UTexture2D? fill = null)
    {
        alpha |= mask != null;
        if (fill != null) alpha = false;
        var key = tex.GetPathName() + (alpha ? "#a" : "") + (mask != null ? "#m" + mask.GetPathName() : "") + (fill != null ? "#f" + fill.GetPathName() : "");
        if (files.TryGetValue(key, out var f)) return f;
        f = null;
        try
        {
            var bmp = tex.Decode(256);
            if (bmp != null)
            {
                if (bmp.Width > 256 || bmp.Height > 256)
                {
                    float sc = 256f / Math.Max(bmp.Width, bmp.Height);
                    var r = bmp.Resize(new SkiaSharp.SKImageInfo(Math.Max(1, (int)(bmp.Width * sc)), Math.Max(1, (int)(bmp.Height * sc))), SkiaSharp.SKFilterQuality.High);
                    bmp.Dispose(); bmp = r;
                }
                if (mask != null)
                {
                    using var mb = mask.Decode(256);
                    if (mb != null)
                    {
                        using var ms = mb.Resize(new SkiaSharp.SKImageInfo(bmp.Width, bmp.Height), SkiaSharp.SKFilterQuality.High);
                        var outBmp = new SkiaSharp.SKBitmap(new SkiaSharp.SKImageInfo(bmp.Width, bmp.Height, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Unpremul));
                        for (int y = 0; y < bmp.Height; y++)
                            for (int x = 0; x < bmp.Width; x++)
                            {
                                var c = bmp.GetPixel(x, y);
                                outBmp.SetPixel(x, y, new SkiaSharp.SKColor(c.Red, c.Green, c.Blue, ms.GetPixel(x, y).Red));
                            }
                        bmp.Dispose(); bmp = outBmp;
                    }
                }
                if (fill != null)
                {
                    using var fb = fill.Decode(256);
                    if (fb != null)
                    {
                        using var fs2 = fb.Resize(new SkiaSharp.SKImageInfo(bmp.Width, bmp.Height), SkiaSharp.SKFilterQuality.High);
                        var outBmp = new SkiaSharp.SKBitmap(new SkiaSharp.SKImageInfo(bmp.Width, bmp.Height, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Unpremul));
                        for (int y = 0; y < bmp.Height; y++)
                            for (int x = 0; x < bmp.Width; x++)
                            {
                                SkiaSharp.SKColor c = bmp.GetPixel(x, y), u = fs2.GetPixel(x, y);
                                float t = c.Alpha / 255f;
                                byte Mix(byte top, byte under) => (byte)Math.Round(top * t + under * (1 - t));
                                outBmp.SetPixel(x, y, new SkiaSharp.SKColor(Mix(c.Red, u.Red), Mix(c.Green, u.Green), Mix(c.Blue, u.Blue), 255));
                            }
                        bmp.Dispose(); bmp = outBmp;
                    }
                }
                uint h = 2166136261; foreach (var ch in key) h = (h ^ ch) * 16777619;
                var safe = Regex.Replace(tex.Name, "[^A-Za-z0-9_-]", "_");
                f = $"{safe}_{h:x8}.{(alpha ? "png" : "jpg")}";
                using var img = SkiaSharp.SKImage.FromBitmap(bmp);
                using var data = alpha ? img.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100) : img.Encode(SkiaSharp.SKEncodedImageFormat.Jpeg, 82);
                using (var fs = File.Create(Path.Combine(dir, f))) data.SaveTo(fs);
                bmp.Dispose();
            }
        }
        catch (Exception e) { Console.Error.WriteLine($"tex {tex.Name}: {e.Message}"); f = null; }
        files[key] = f;
        return f;
    }
}

// Minimal GLB writer: unique meshes + one node per instance with a matrix.
class GlbWriter
{
    readonly MemoryStream bin = new();
    readonly List<object> bufferViews = new(), accessors = new(), meshes = new(), nodes = new(), materials = new();
    readonly Dictionary<string, int> matIndex = new();
    readonly Dictionary<(string mesh, string matsKey), int> meshIndex = new();
    readonly TextureStore? texStore;
    public GlbWriter(TextureStore? texStore = null) { this.texStore = texStore; }

    int AddView(byte[] data, int target)
    {
        while (bin.Length % 4 != 0) bin.WriteByte(0);
        var off = (int)bin.Length;
        bin.Write(data);
        bufferViews.Add(new Dictionary<string, object> { ["buffer"] = 0, ["byteOffset"] = off, ["byteLength"] = data.Length, ["target"] = target });
        return bufferViews.Count - 1;
    }

    int Material(MaterialColors.MatInfo mi)
    {
        // Untextured materials with near-identical color are merged to keep draw calls down.
        // The material name carries what the viewer needs as JSON: c = flat color, t = texture file, k = tint, m = masked, s = tiling,
        // u = unlit (drawn without lighting, like the game's unlit materials).
        int q(float v) => (int)Math.Round(Math.Clamp(v, 0, 1) * 31);
        string hex(float r, float g, float b) => $"{(int)(Math.Clamp(r, 0, 1) * 255):x2}{(int)(Math.Clamp(g, 0, 1) * 255):x2}{(int)(Math.Clamp(b, 0, 1) * 255):x2}";
        string? texFile = mi.Tex != null && texStore != null && mi.A >= 1 ? texStore.Get(mi.Tex, mi.Masked, mi.MaskTex, mi.FillTex) : null;
        var info = new Dictionary<string, object> { ["c"] = hex(mi.R, mi.G, mi.B) };
        if (texFile != null)
        {
            info["t"] = texFile;
            var tint = mi.Tint ?? Vector3.One;
            info["k"] = hex(tint.X, tint.Y, tint.Z);
            if (mi.Masked) info["m"] = 1;
            if (Math.Abs(mi.Tile - 1f) > 1e-3f) info["s"] = Math.Round(mi.Tile, 3);
        }
        else info["c"] = hex(q(mi.R) / 31f, q(mi.G) / 31f, q(mi.B) / 31f);
        if (mi.Unlit) info["u"] = 1;
        var name = Newtonsoft.Json.JsonConvert.SerializeObject(info);
        var key = name + (mi.A < 1 ? "t" : "o");
        if (matIndex.TryGetValue(key, out var i)) return i;
        float lin(float c) => c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
        var m = new Dictionary<string, object>
        {
            ["name"] = name,
            ["pbrMetallicRoughness"] = new Dictionary<string, object> { ["baseColorFactor"] = new[] { lin(mi.R), lin(mi.G), lin(mi.B), mi.A }, ["metallicFactor"] = 0f, ["roughnessFactor"] = 1f },
            ["doubleSided"] = true,
        };
        if (mi.A < 1) m["alphaMode"] = "BLEND";
        materials.Add(m);
        matIndex[key] = materials.Count - 1;
        return materials.Count - 1;
    }

    public long Tris;

    static readonly Regex SkipMesh = new(@"(^|_)sky(_|$)|sky_sm|endsky|intro_ocean|skydome|skysphere|teleporter_tent", RegexOptions.IgnoreCase);
    public int Skipped;

    public void AddInstance(MeshData md, List<MaterialColors.MatInfo> mats, Matrix4x4 worldUe, string nodeName)
    {
        var meshName = md.Path.Split('.').Last();
        // (water_shadowdoer, the dark unlit floor 13 m under the sea surface, is kept: it is what hides the depths in the game.)
        if (SkipMesh.IsMatch(meshName)) { Skipped++; return; }
        // Anything wider than 3 km is a backdrop, not playable space.
        if (md.Pos.Length >= 3)
        {
            float mnx = float.MaxValue, mxx = float.MinValue, mnz = float.MaxValue, mxz = float.MinValue;
            for (int i = 0; i < md.Pos.Length; i += 3) { mnx = Math.Min(mnx, md.Pos[i]); mxx = Math.Max(mxx, md.Pos[i]); mnz = Math.Min(mnz, md.Pos[i + 2]); mxz = Math.Max(mxz, md.Pos[i + 2]); }
            float sx = new Vector3(worldUe.M11, worldUe.M12, worldUe.M13).Length(), sy = new Vector3(worldUe.M21, worldUe.M22, worldUe.M23).Length();
            if (Math.Max((mxx - mnx) * sx, (mxz - mnz) * sy) > 300000) { Skipped++; return; }
        }
        var matsKey = string.Join("|", mats.Select(m => m.Key + (m.Skip ? "!" : "")));
        if (!meshIndex.TryGetValue((md.Path, matsKey), out var mesh))
        {
            mesh = -1;
            var prims = new List<object>();
            int posAcc = -1, uvAcc = -1;
            for (int si = 0; si < md.Sections.Count; si++)
            {
                var (idx, slot) = md.Sections[si];
                var mi = slot < mats.Count ? mats[slot] : new MaterialColors.MatInfo(0.6f, 0.6f, 0.6f, 1, false, "none");
                if (mi.Skip) continue;
                if (posAcc < 0) posAcc = PosAccessor(md.Pos);
                if (uvAcc < 0 && md.Uv != null) uvAcc = UvAccessor(md.Uv);
                var ib = new byte[idx.Length * 4];
                Buffer.BlockCopy(idx, 0, ib, 0, ib.Length);
                var iv = AddView(ib, 34963);
                accessors.Add(new Dictionary<string, object> { ["bufferView"] = iv, ["componentType"] = 5125, ["count"] = idx.Length, ["type"] = "SCALAR" });
                var attrs = new Dictionary<string, int> { ["POSITION"] = posAcc };
                if (uvAcc >= 0) attrs["TEXCOORD_0"] = uvAcc;
                prims.Add(new Dictionary<string, object> { ["attributes"] = attrs, ["indices"] = accessors.Count - 1, ["material"] = Material(mi) });
            }
            if (prims.Count > 0)
            {
                meshes.Add(new Dictionary<string, object> { ["name"] = md.Path.Split('.').Last(), ["primitives"] = prims });
                mesh = meshes.Count - 1;
            }
            meshIndex[(md.Path, matsKey)] = mesh;
        }
        if (mesh < 0) return;
        // Convert UE row-vector world matrix to glTF: R = C W C (C swaps Y/Z), stored row-major == glTF column-major.
        var C = new Matrix4x4(1, 0, 0, 0, 0, 0, 1, 0, 0, 1, 0, 0, 0, 0, 0, 1);
        var r = C * worldUe * C;
        var arr = new[] { r.M11, r.M12, r.M13, r.M14, r.M21, r.M22, r.M23, r.M24, r.M31, r.M32, r.M33, r.M34, r.M41, r.M42, r.M43, r.M44 };
        nodes.Add(new Dictionary<string, object> { ["mesh"] = mesh, ["matrix"] = arr, ["name"] = nodeName + "|" + meshName });
        Tris += md.Tris;
    }

    int UvAccessor(float[] uv)
    {
        var b = new byte[uv.Length * 4];
        Buffer.BlockCopy(uv, 0, b, 0, b.Length);
        var v = AddView(b, 34962);
        accessors.Add(new Dictionary<string, object> { ["bufferView"] = v, ["componentType"] = 5126, ["count"] = uv.Length / 2, ["type"] = "VEC2" });
        return accessors.Count - 1;
    }

    int PosAccessor(float[] pos)
    {
        var b = new byte[pos.Length * 4];
        Buffer.BlockCopy(pos, 0, b, 0, b.Length);
        var v = AddView(b, 34962);
        float[] mn = { float.MaxValue, float.MaxValue, float.MaxValue }, mx = { float.MinValue, float.MinValue, float.MinValue };
        for (int i = 0; i < pos.Length; i++) { mn[i % 3] = Math.Min(mn[i % 3], pos[i]); mx[i % 3] = Math.Max(mx[i % 3], pos[i]); }
        accessors.Add(new Dictionary<string, object> { ["bufferView"] = v, ["componentType"] = 5126, ["count"] = pos.Length / 3, ["type"] = "VEC3", ["min"] = mn, ["max"] = mx });
        return accessors.Count - 1;
    }

    public int NodeCount => nodes.Count;

    public void Save(string path)
    {
        while (bin.Length % 4 != 0) bin.WriteByte(0);
        var gltf = new Dictionary<string, object>
        {
            ["asset"] = new Dictionary<string, string> { ["version"] = "2.0", ["generator"] = "qdmap-extractor" },
            ["scene"] = 0,
            ["scenes"] = new[] { new Dictionary<string, object> { ["nodes"] = Enumerable.Range(0, nodes.Count).ToArray() } },
            ["nodes"] = nodes, ["meshes"] = meshes, ["materials"] = materials, ["accessors"] = accessors, ["bufferViews"] = bufferViews,
            ["buffers"] = new[] { new Dictionary<string, object> { ["byteLength"] = bin.Length } },
        };
        var json = Encoding.UTF8.GetBytes(Newtonsoft.Json.JsonConvert.SerializeObject(gltf));
        int jsonPad = (4 - json.Length % 4) % 4;
        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);
        int total = 12 + 8 + json.Length + jsonPad + 8 + (int)bin.Length;
        w.Write(0x46546C67); w.Write(2); w.Write(total);
        w.Write(json.Length + jsonPad); w.Write(0x4E4F534A); w.Write(json); for (int i = 0; i < jsonPad; i++) w.Write((byte)' ');
        w.Write((int)bin.Length); w.Write(0x004E4942); w.Write(bin.ToArray());
    }
}
