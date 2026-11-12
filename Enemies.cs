using System.Numerics;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse_Conversion.Meshes;

// Exports one model per enemy type (EnemyData asset) for the map's enemy layer, posed with frame 0 of an idle animation
// made for its skeleton (rest pose when there is none). out/enemies_test.glb has every model in a row for checking.
static class Enemies
{
    public static void Run(DefaultFileProvider provider, string outDir)
    {
        var dir = Path.Combine(outDir, "enemies");
        Directory.CreateDirectory(dir);
        var texStore = new TextureStore(Path.Combine(outDir, "tex"));
        var mats = new MaterialColors();
        var index = new Dictionary<string, object>();
        var log = new List<string>();
        var idles = IdleIndex(provider, log);
        var testW = new GlbWriter(texStore);
        var cat = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(Path.Combine(outDir, "catalog_raw.json")));
        foreach (var kv in cat)
        {
            if ((string?)kv.Value!["type"] != "EnemyData_C") continue;
            var key = kv.Key.Split('.').Last();
            var props = kv.Value["props"]!;
            var bpPath = (string?)props["VisualEnemyBPOverride"] ?? (string?)props["EnemyBP"];
            if (string.IsNullOrEmpty(bpPath) || bpPath == "None") continue;
            try
            {
                var res = Export(provider, bpPath, key, dir, texStore, mats, idles, log, testW);
                if (res != null) { index[key] = res; Console.WriteLine($"{key,-40} {res["tris"],7} tris  {res["pose"]}"); }
                else Console.WriteLine($"{key,-40} (no mesh) {bpPath.Split('/').Last()}");
            }
            catch (Exception e) { Console.WriteLine($"{key}: {e.GetType().Name} {e.Message}"); }
        }
        File.WriteAllText(Path.Combine(dir, "index.json"), Newtonsoft.Json.JsonConvert.SerializeObject(index, Newtonsoft.Json.Formatting.Indented));
        testW.Save(Path.Combine(outDir, "enemies_test.glb"));
        File.WriteAllLines(Path.Combine(outDir, "enemies_pose_log.txt"), log);
        Console.WriteLine($"enemy models: {index.Count}, idle-posed: {index.Values.Count(v => ((Dictionary<string, object>)v)["pose"] as string != "rest")}, textures: {texStore.Count}");
    }

    // Every non-additive idle animation in the game, grouped by the skeleton it was made for.
    internal static Dictionary<string, List<UAnimSequence>> IdleIndex(DefaultFileProvider provider, List<string> log)
    {
        var res = new Dictionary<string, List<UAnimSequence>>();
        foreach (var f in provider.Files.Keys)
        {
            if (!f.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)) continue;
            var name = Path.GetFileNameWithoutExtension(f);
            if (!name.Contains("idle", StringComparison.OrdinalIgnoreCase) || System.Text.RegularExpressions.Regex.IsMatch(name, "montage|_cue|sound|audio", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) continue;
            try
            {
                var anim = provider.LoadPackage(f[..^7]).GetExports().OfType<CUE4Parse.UE4.Assets.Exports.Animation.UAnimSequence>().FirstOrDefault();
                if (anim == null || anim.AdditiveAnimType.ToString() != "AAT_None") continue;
                var sk = anim.Skeleton?.ResolvedObject?.GetPathName();
                if (sk == null) continue;
                if (!res.TryGetValue(sk, out var l)) res[sk] = l = new();
                l.Add(anim);
            }
            catch (Exception e) { log.Add($"idle {name}: {e.Message}"); }
        }
        log.Add($"idle animations: {res.Values.Sum(l => l.Count)} for {res.Count} skeletons");
        return res;
    }

    // The idle made for this mesh's skeleton whose name best matches the enemy (Boner_Gun_Idle for a gunner skeleton),
    // else the plainest one ("..._Idle" over "Idle_Fast").
    internal static CUE4Parse.UE4.Assets.Exports.Animation.UAnimSequence? PickIdle(Dictionary<string, List<CUE4Parse.UE4.Assets.Exports.Animation.UAnimSequence>> idles, USkeletalMesh mesh, string enemyName)
    {
        var sk = mesh.Skeleton?.ResolvedObject?.GetPathName();
        if (sk == null || !idles.TryGetValue(sk, out var list)) return null;
        var en = enemyName.ToLowerInvariant();
        int Score(CUE4Parse.UE4.Assets.Exports.Animation.UAnimSequence a)
        {
            int s = 0;
            foreach (var t in System.Text.RegularExpressions.Regex.Split(a.Name.ToLowerInvariant(), "[^a-z]+").Distinct())
            {
                if (t.Length < 3 || t == "idle") continue;
                // Death, movement and prop-holding variants only for enemies named that way (an "UndeadKnight" is not crushed and dead).
                bool special = System.Text.RegularExpressions.Regex.IsMatch(t, "^(dead|death|crushed|sleep|run|walk|attack|bomb|potion|holding|aggro|ghost)$");
                if (en.Contains(t) && !(special && t is "dead" or "aggro")) s += 3;
                else if (special) s -= 3;
            }
            if (a.Name.EndsWith("idle", StringComparison.OrdinalIgnoreCase)) s += 1;
            if (System.Text.RegularExpressions.Regex.IsMatch(a.Name, "fast|look|alert|combat|break|bored|var|_[0-9]+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) s -= 1;
            return s;
        }
        return list.OrderByDescending(Score).ThenBy(a => a.Name.Length).First();
    }

    static UObject? LoadClass(DefaultFileProvider provider, string path)
    {
        // "QueensDomain/Content/.../X_BP.X_BP_C" or "/Game/.../X_BP.X_BP_C"
        path = path.Replace("/Game/", "QueensDomain/Content/");
        var pkg = path.Split('.')[0];
        var name = path.Split('.').Last();
        return provider.LoadPackage(pkg).GetExports().FirstOrDefault(e => e.Name == name);
    }

    // Find the skeletal mesh component on the class default object, walking up the blueprint parents.
    static (UObject comp, USkeletalMesh mesh)? FindMeshComponent(UObject cls)
    {
        for (UStruct? c = cls as UStruct; c != null; c = SafeSuper(c))
        {
            var pkgExports = c.Owner?.GetExports().ToList() ?? new();
            var cdoName = "Default__" + c.Name;
            var cands = pkgExports.Where(e => e.ExportType.Contains("SkeletalMeshComponent") &&
                (e.Outer?.Name == cdoName || e.Name.EndsWith("_GEN_VARIABLE"))).ToList();
            // Prefer the character's main mesh.
            cands = cands.OrderByDescending(e => e.Name.StartsWith("CharacterMesh") ? 2 : e.Name.Contains("Mesh") ? 1 : 0).ToList();
            foreach (var comp in cands)
            {
                if (Ue.Obj(comp, "SkeletalMesh") is USkeletalMesh sm) return (comp, sm);
            }
        }
        return null;
    }

    static UStruct? SafeSuper(UStruct s) { try { return s.SuperStruct?.Load<UStruct>(); } catch { return null; } }

    static float CapsuleHalfHeight(UObject cls)
    {
        for (UStruct? c = cls as UStruct; c != null; c = SafeSuper(c))
        {
            var cdoName = "Default__" + c.Name;
            var cap = c.Owner?.GetExports().FirstOrDefault(e => e.ExportType == "CapsuleComponent" && e.Outer?.Name == cdoName);
            if (cap != null && Ue.Val(cap, "CapsuleHalfHeight") is float h) return h;
        }
        return 88f;
    }

    static Dictionary<string, object>? Export(DefaultFileProvider provider, string bpPath, string key, string dir, TextureStore texStore, MaterialColors mats,
        Dictionary<string, List<CUE4Parse.UE4.Assets.Exports.Animation.UAnimSequence>> idles, List<string> log, GlbWriter testW)
    {
        var cls = LoadClass(provider, bpPath);
        if (cls == null) return null;
        var found = FindMeshComponent(cls);
        if (found == null) return null;
        var (comp, skel) = found.Value;
        var anim = PickIdle(idles, skel, key + " " + bpPath.Split('/').Last());
        var md = Skeletons.Pose(skel, anim, false, skel.GetPathName() + "|" + (anim?.GetPathName() ?? "rest"), log);
        if (md == null && anim != null) { log.Add($"{key}: posing with {anim.Name} failed, rest pose"); anim = null; md = Skeletons.Pose(skel, null, false, skel.GetPathName() + "|rest", log); }
        if (md == null) return null;
        if (anim == null) log.Add($"{key}: no idle animation for skeleton {skel.Skeleton?.ResolvedObject?.Name}, rest pose");
        // Materials: component overrides (through the template chain) win over the mesh's own.
        var overrides = new List<UObject?>();
        if (Ue.Val(comp, "OverrideMaterials") is UScriptArray oa) overrides = oa.Properties.Select(p => Ue.LoadRef(p.GenericValue)).ToList();
        var meshMats = new List<UObject?>();
        try { if (skel.Materials != null) foreach (var m in skel.Materials) meshMats.Add(m?.Load()); } catch { }
        try { if (meshMats.Count == 0) foreach (var m in skel.SkeletalMaterials ?? Array.Empty<FSkeletalMaterial>()) meshMats.Add(m.Material?.Load()); } catch { }
        int slots = Math.Max(meshMats.Count, md.Sections.Count == 0 ? 0 : md.Sections.Max(s => s.MatSlot) + 1);
        var infos = new List<MaterialColors.MatInfo>();
        for (int i = 0; i < slots; i++)
        {
            var m = i < overrides.Count && overrides[i] != null ? overrides[i] : (i < meshMats.Count ? meshMats[i] : null);
            var mi = mats.Get(m);
            infos.Add(mi with { Skip = false, A = 1 });
        }
        // Mesh is relative to the capsule centre; the spawner sits on the ground, so lift by the capsule half height.
        var rel = Ue.Relative(comp);
        var world = rel * Matrix4x4.CreateTranslation(0, 0, CapsuleHalfHeight(cls));
        var w = new GlbWriter(texStore);
        w.AddInstance(md, infos, world, key);
        if (w.NodeCount == 0) return null;
        var file = key + ".glb";
        w.Save(Path.Combine(dir, file));
        testW.AddInstance(md, infos, world * Matrix4x4.CreateTranslation(testW.NodeCount * 600f, 0, 0), (anim?.Name ?? "rest") + "|" + key);
        return new Dictionary<string, object> { ["glb"] = file, ["tris"] = md.Tris, ["pose"] = anim?.Name ?? "rest" };
    }
}
