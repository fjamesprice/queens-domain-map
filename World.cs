using System.Numerics;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.Engine;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Assets.Exports.Component.StaticMesh;

record LevelRef(string Name, string PackagePath, FVector Offset, bool InitiallyLoaded);

record SplineInfo(Vector3 StartPos, Vector3 StartTan, Vector2 StartScale, float StartRoll, Vector2 StartOffset,
                  Vector3 EndPos, Vector3 EndTan, Vector2 EndScale, float EndRoll, Vector2 EndOffset, int ForwardAxis, Vector3 UpDir, bool Smooth);

record MeshInstance(UStaticMesh Mesh, Matrix4x4 World, UObject Component, UObject? Actor, List<UObject?> OverrideMaterials, SplineInfo? Spline = null);

class LevelData
{
    public required LevelRef Ref;
    public List<Dictionary<string, object?>> Actors = new();
    public List<MeshInstance> Meshes = new();
    public Dictionary<string, Dictionary<string, object?>> ClassDefaults = new();
}

static class WorldReader
{
    static readonly Dictionary<string, UStaticMesh?> meshObjCache = new();

    public static UStaticMesh? LoadMesh(UObject c)
    {
        var v = Ue.Val(c, "StaticMesh");
        if (v is not FPackageIndex pi || pi.IsNull) return null;
        string key;
        try { key = pi.ResolvedObject?.GetPathName() ?? ""; } catch { return null; }
        if (key.Contains("/EditorMeshes/") || key.Contains("EngineDebug")) return null;
        if (meshObjCache.TryGetValue(key, out var sm)) return sm;
        try { sm = pi.Load() as UStaticMesh; } catch { sm = null; }
        meshObjCache[key] = sm;
        return sm;
    }

    // Menu, empty, intro and cutscene-only levels: not part of the explorable world.
    public static readonly HashSet<string> SkipLevels = new() { "060_Ocean_Shadow", "000_Shared_Base_Lighting",
        "Intro_Level_00", "Intro_Level_01", "997_FinalZoneCutscene", "998_QueenCutscenes", "999_SipehaCutscenes" };

    public const string Persistent = "QueensDomain/Content/RuinsLevel/Maps/Ruins_Level_00";

    public static List<LevelRef> Levels(AbstractFileProvider provider)
    {
        var res = new List<LevelRef> { new("Ruins_Level_00", Persistent, new FVector(0, 0, 0), true) };
        foreach (var e in provider.LoadPackage(Persistent).GetExports())
        {
            if (!e.ExportType.StartsWith("LevelStreaming")) continue;
            var wa = Ue.Val(e, "WorldAsset");
            string? path = wa is FSoftObjectPath sp ? sp.AssetPathName.Text : null;
            if (path == null) continue;
            var off = new FVector(0, 0, 0);
            var lt = Ue.Val(e, "LevelTransform");
            if (lt is FScriptStruct ss && ss.StructType is FStructFallback fb)
            {
                foreach (var p in fb.Properties)
                    if (p.Name.Text == "Translation" && p.Tag?.GenericValue is FScriptStruct t && t.StructType is FVector v) off = v;
            }
            else if (lt is FScriptStruct ss2 && ss2.StructType is FTransform ft) off = ft.Translation;
            var pkg = path.Split('.')[0].Replace("/Game/", "QueensDomain/Content/");
            var name = pkg.Split('/').Last();
            bool init = Ue.Bool(e, "bInitiallyLoaded", false) || e.ExportType == "LevelStreamingAlwaysLoaded";
            res.Add(new(name, pkg, off, init));
        }
        return res;
    }

    static readonly HashSet<string> SkipActorProps = new()
    {
        "BlueprintCreatedComponents", "RootComponent", "UberGraphFrame", "CurrentFogAlphaDA", "CurrentFogBetaDA",
        "AlphaFog", "BetaFog", "FogDA", "InstanceComponents", "bCanBeInCluster", "ActorUROManager", "LineOfSightCheckerComp",
        "SaveHelper", "DefaultSceneRoot", "GUIDs", "GUID"
    };

    public static LevelData Read(AbstractFileProvider provider, LevelRef lr, bool wantMeshes)
    {
        var ld = new LevelData { Ref = lr };
        var pkg = provider.LoadPackage(lr.PackagePath);
        var exports = pkg.GetExports().ToList();
        var level = exports.OfType<ULevel>().FirstOrDefault();
        if (level == null) { Console.Error.WriteLine($"{lr.Name}: no ULevel"); return ld; }
        var levelXf = Matrix4x4.CreateTranslation(lr.Offset.X, lr.Offset.Y, lr.Offset.Z);
        var cache = new Dictionary<UObject, Matrix4x4>();

        // components grouped by owning actor (Outer)
        var byOuter = new Dictionary<UObject, List<UObject>>();
        foreach (var e in exports)
        {
            if (e.Outer is UObject o)
            {
                if (!byOuter.TryGetValue(o, out var l)) byOuter[o] = l = new();
                l.Add(e);
            }
        }

        foreach (var ai in level.Actors)
        {
            UObject? actor;
            try { actor = ai.IsNull ? null : ai.Load(); } catch { continue; }
            if (actor == null) continue;
            var anc = Ue.Ancestry(actor.Class);
            var root = Ue.Obj(actor, "RootComponent");
            Vector3 pos = Vector3.Zero; float yaw = 0;
            Matrix4x4 rootW = levelXf;
            if (root != null)
            {
                rootW = Ue.World(root, cache, levelXf);
                pos = rootW.Translation;
                yaw = (float)(Math.Atan2(rootW.M12, rootW.M11) * 180 / Math.PI);
            }
            var props = new Dictionary<string, object?>();
            foreach (var t in actor.Properties)
            {
                var n = t.Name.Text;
                if (SkipActorProps.Contains(n)) continue;
                var gv = t.Tag?.GenericValue;
                // skip refs to own components
                if (gv is FPackageIndex pi && !pi.IsNull)
                {
                    try { var r = pi.ResolvedObject; if (r != null && r.GetPathName().Contains("." + actor.Name + ".")) continue; } catch { }
                }
                var s = Ue.Simplify(gv);
                if (s != null) props[n] = s;
            }
            // Bounds of the actor's visible meshes (for markers sized to objects)
            var cn = actor.Class?.Name ?? "?";
            if (!ld.ClassDefaults.ContainsKey(cn))
            {
                var cdo = Ue.Tmpl(actor);
                ld.ClassDefaults[cn] = cdo != null ? Ue.MergedProps(cdo, SkipActorProps) : new();
            }
            var rec = new Dictionary<string, object?>
            {
                ["name"] = actor.Name,
                ["class"] = actor.Class?.Name,
                ["anc"] = anc,
                ["pos"] = new[] { pos.X, pos.Y, pos.Z },
                ["yaw"] = yaw,
                ["props"] = props,
            };
            ld.Actors.Add(rec);

            if (!wantMeshes) continue;
            if (!byOuter.TryGetValue(actor, out var comps)) continue;
            bool actorHidden = Ue.Bool(actor, "bHidden", false);
            if (actorHidden) continue;
            foreach (var c in comps)
            {
                if (c is not UStaticMeshComponent && !c.ExportType.Contains("StaticMeshComponent")) continue;
                if (c.ExportType.Contains("Instanced")) { AddInstanced(ld, c, actor, cache, levelXf); continue; }
                if (!Ue.Bool(c, "bVisible", true) || Ue.Bool(c, "bHiddenInGame", false)) continue;
                if (System.Text.RegularExpressions.Regex.IsMatch(c.Name, "Visualizer|Preview|EditorOnly|Gizmo|Arrow", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) continue;
                var sm = LoadMesh(c);
                if (sm == null) continue;
                var w = Ue.World(c, cache, levelXf);
                ld.Meshes.Add(new(sm, w, c, actor, OverrideMats(c), ReadSpline(c)));
            }
        }
        return ld;
    }

    static SplineInfo? ReadSpline(UObject c)
    {
        if (Ue.FindTag(c, "SplineParams") == null && !c.ExportType.Contains("Spline")) return null;
        var sp = Ue.Val(c, "SplineParams");
        var d = new Dictionary<string, object?>();
        if (sp is FScriptStruct ss && ss.StructType is FStructFallback fb)
            foreach (var p in fb.Properties) d[p.Name.Text] = p.Tag?.GenericValue;
        Vector3 V(string k, Vector3 def) => d.TryGetValue(k, out var v) && v is FScriptStruct s2 && s2.StructType is FVector fv ? new Vector3(fv.X, fv.Y, fv.Z) : def;
        Vector2 V2(string k, Vector2 def) => d.TryGetValue(k, out var v) && v is FScriptStruct s2 && s2.StructType is FVector2D fv ? new Vector2(fv.X, fv.Y) : def;
        float F(string k, float def) => d.TryGetValue(k, out var v) && v is float f ? f : def;
        var fa = Ue.Val(c, "ForwardAxis")?.ToString() ?? "";
        int axis = fa.EndsWith("Y") ? 1 : fa.EndsWith("Z") ? 2 : 0;
        var up = Ue.Vec(c, "SplineUpDir", new FVector(0, 0, 1));
        bool smooth = Ue.Bool(c, "bSmoothInterpRollScale", false);
        return new SplineInfo(V("StartPos", Vector3.Zero), V("StartTangent", new Vector3(100, 0, 0)), V2("StartScale", Vector2.One), F("StartRoll", 0), V2("StartOffset", Vector2.Zero),
            V("EndPos", new Vector3(100, 0, 0)), V("EndTangent", new Vector3(100, 0, 0)), V2("EndScale", Vector2.One), F("EndRoll", 0), V2("EndOffset", Vector2.Zero),
            axis, new Vector3(up.X, up.Y, up.Z), smooth);
    }

    static List<UObject?> OverrideMats(UObject c)
    {
        var v = Ue.Val(c, "OverrideMaterials");
        if (v is UScriptArray arr) return arr.Properties.Select(p => Ue.LoadRef(p.GenericValue)).ToList();
        return new();
    }

    static void AddInstanced(LevelData ld, UObject c, UObject actor, Dictionary<UObject, Matrix4x4> cache, Matrix4x4 levelXf)
    {
        if (!Ue.Bool(c, "bVisible", true) || Ue.Bool(c, "bHiddenInGame", false)) return;
        var sm = LoadMesh(c);
        if (sm == null) return;
        var w = Ue.World(c, cache, levelXf);
        if (c is UInstancedStaticMeshComponent ism && ism.PerInstanceSMData != null)
        {
            foreach (var inst in ism.PerInstanceSMData)
            {
                var t = inst.TransformData;
                var m = Ue.Srt(t.Scale3D, t.Rotation.Rotator(), t.Translation);
                ld.Meshes.Add(new(sm, m * w, c, actor, OverrideMats(c)));
            }
        }
    }
}
