using System.Numerics;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.Engine;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse_Conversion.Animations;
using CUE4Parse_Conversion.Animations.PSA;
using CUE4Parse_Conversion.Meshes;

// Posed props: each placement's skeletal mesh is skinned on the CPU into the still pose its component plays
// (AnimationData.AnimToPlay, frame 0) and written as static geometry, one GLB per level. These are the skeletons
// (Boner_TrapDecoy), and everything else placed the same way: gargoyles, dead knights, armor and creature statues,
// decoy robots, the Petrified Garden's statues.
static class Skeletons
{
    // Actor classes whose posed skeletal mesh is exported: anything descended from these...
    static readonly string[] PropBases = { "Boner_TrapDecoy_C", "FS_StationarySkeletalMeshActor", "SkeletalMeshActor" };
    // ...plus the gargoyle switches (a plain actor whose posed mesh is a child component), but no cutscene actors.
    static readonly string[] PropExtra = { "Gargoyle_Switch_C" };
    static readonly string[] NotProps = { "CutsceneSkeletalMeshActor_C", "FS_SequenceCharacter_C" };
    // Only skeletons get Skeleton markers (build_data.py reads "placed"); other props are listed under "props".
    const string SkeletonMeshFolder = "/Enemies/Boner_00/";

    public static void Run(DefaultFileProvider provider, string outDir, HashSet<string> only)
    {
        var dir = Path.Combine(outDir, "skel");
        Directory.CreateDirectory(dir);
        var texStore = new TextureStore(Path.Combine(outDir, "tex"));
        var mats = new MaterialColors();
        var posed = new Dictionary<string, MeshData?>();
        var testW = new GlbWriter(texStore);
        var testSeen = new HashSet<string>();
        var log = new List<string>();
        var index = new Dictionary<string, object>();
        var placed = new List<string[]>(); // [level, actor, pose] for every exported skeleton (build_data.py uses it for markers)
        var props = new List<string[]>();  // [level, actor, class, mesh, pose] for the other posed props
        Dictionary<string, List<UAnimSequence>>? idles = null;
        int total = 0;
        foreach (var lr in WorldReader.Levels(provider))
        {
            if (only.Count > 0 && !only.Contains(lr.Name)) continue;
            if (WorldReader.SkipLevels.Contains(lr.Name)) continue;
            var pkg = provider.LoadPackage(lr.PackagePath);
            var exports = pkg.GetExports().ToList();
            var level = exports.OfType<ULevel>().FirstOrDefault();
            if (level == null) continue;
            var levelXf = Matrix4x4.CreateTranslation(lr.Offset.X, lr.Offset.Y, lr.Offset.Z);
            var cache = new Dictionary<UObject, Matrix4x4>();
            var w = new GlbWriter(texStore);
            int n = 0;
            foreach (var ai in level.Actors)
            {
                UObject? actor;
                try { actor = ai.IsNull ? null : ai.Load(); } catch { continue; }
                if (actor == null) continue;
                var anc = Ue.Ancestry(actor.Class);
                if (!anc.Any(a => PropBases.Contains(a) || PropExtra.Contains(a)) || anc.Any(NotProps.Contains)) continue;
                if (Ue.Bool(actor, "bHidden", false)) continue;
                // The posed mesh is the root on the skeleton props, a child of a scene root on the gargoyle switches.
                var comp = Ue.Obj(actor, "RootComponent");
                if (comp != null && !comp.ExportType.Contains("SkeletalMeshComponent"))
                    comp = exports.FirstOrDefault(e => e.Outer == actor && e.ExportType.Contains("SkeletalMeshComponent"));
                if (comp == null || !Ue.Bool(comp, "bVisible", true) || Ue.Bool(comp, "bHiddenInGame", false)) continue;
                var mesh = Ue.Obj(comp, "SkeletalMesh") as USkeletalMesh;
                if (mesh == null) { log.Add($"{lr.Name} {actor.Name}: no mesh"); continue; }
                bool isSkeleton = mesh.GetPathName().Contains(SkeletonMeshFolder);
                UAnimSequence? anim = null;
                if (Ue.Val(comp, "AnimationData") is FScriptStruct ss && ss.StructType is FStructFallback fb)
                    foreach (var p in fb.Properties)
                        if (p.Name.Text == "AnimToPlay") anim = Ue.LoadRef(p.Tag?.GenericValue) as UAnimSequence;
                // No still pose set: an idle made for the mesh's skeleton, as for NPCs, rather than the bind (T) pose.
                if (anim == null && !isSkeleton)
                {
                    idles ??= Enemies.IdleIndex(provider, log);
                    anim = Enemies.PickIdle(idles, mesh, actor.Class?.Name + " " + actor.Name);
                    log.Add($"{lr.Name} {actor.Name} ({actor.Class?.Name}): no AnimToPlay, {mesh.Name} posed with {anim?.Name ?? "rest pose"}");
                }
                bool hideHead = Ue.Bool(actor, "HideHead", false);
                var key = mesh.GetPathName() + "|" + (anim?.GetPathName() ?? "rest") + (hideHead ? "|nohead" : "");
                if (!posed.TryGetValue(key, out var md))
                {
                    try { md = Pose(mesh, anim, hideHead, key, log); }
                    catch (Exception e) { log.Add($"{key}: {e.GetType().Name} {e.Message}"); md = null; }
                    posed[key] = md;
                }
                if (md == null) continue;
                var infos = MatInfos(comp, mesh, md, mats);
                var world = Ue.World(comp, cache, levelXf);
                w.AddInstance(md, infos, world, actor.Name + "|" + (anim?.Name ?? "rest"));
                if (isSkeleton) placed.Add(new[] { lr.Name, actor.Name, anim?.Name ?? "" });
                else props.Add(new[] { lr.Name, actor.Name, actor.Class?.Name ?? "", mesh.Name, anim?.Name ?? "" });
                n++;
                if (testSeen.Add(key))
                {
                    int k = testSeen.Count - 1;
                    var at = Matrix4x4.CreateTranslation((k % 16) * 250f, (k / 16) * 250f, 0);
                    testW.AddInstance(md, infos, at, (anim?.Name ?? "rest") + "|" + mesh.Name);
                }
            }
            if (n == 0) continue;
            w.Save(Path.Combine(dir, lr.Name + ".glb"));
            index[lr.Name] = new Dictionary<string, object> { ["count"] = n, ["tris"] = w.Tris };
            total += n;
            Console.WriteLine($"{lr.Name,-28} posed props={n} tris={w.Tris}");
        }
        testW.Save(Path.Combine(outDir, "skel_test.glb"));
        File.WriteAllText(Path.Combine(dir, "index.json"), Newtonsoft.Json.JsonConvert.SerializeObject(new Dictionary<string, object> { ["levels"] = index, ["placed"] = placed, ["props"] = props }, Newtonsoft.Json.Formatting.Indented));
        File.WriteAllLines(Path.Combine(outDir, "skeletons_log.txt"), log);
        Console.WriteLine($"posed props: {total} ({placed.Count} skeletons), posed meshes: {posed.Count(p => p.Value != null)}, test combos: {testSeen.Count}, log lines: {log.Count}");
    }

    // Wildlife (lizards, crabs, bugs, rats, birds): placed critter actors whose skeletal meshes are driven by an anim
    // blueprint. Each is posed with frame 0 of its family's idle animation, the way it sits until the player spooks it.
    static readonly string[] WildBases = { "Lizard_Base_BP_C", "Crab_Base_BP_C", "Bug_Base_BP_C", "Rat_Base_BP_C", "FS_CrowBase", "Crow_Bp_C" };
    static readonly Dictionary<string, string> WildIdle = new()
    {
        ["LizardCritter"] = "QueensDomain/Content/NPCs/Lizard/LizardCritter_Idle",
        ["CrabCritter"] = "QueensDomain/Content/NPCs/Crab/CrabCritter_Idle",
        ["Bug"] = "QueensDomain/Content/NPCs/Bug/Bug_Idle",
        ["Rat"] = "QueensDomain/Content/NPCs/Rat/Rat_Idle_Look",
        ["Crow_Mesh"] = "QueensDomain/Content/NPCs/Crow/Crow_Standing",
    };

    public static void RunWildlife(DefaultFileProvider provider, string outDir, HashSet<string> only)
    {
        var dir = Path.Combine(outDir, "wild");
        Directory.CreateDirectory(dir);
        var texStore = new TextureStore(Path.Combine(outDir, "tex"));
        var mats = new MaterialColors();
        var posed = new Dictionary<string, MeshData?>();
        var animByPath = new Dictionary<string, UAnimSequence?>();
        var testW = new GlbWriter(texStore);
        var testSeen = new HashSet<string>();
        var log = new List<string>();
        var index = new Dictionary<string, object>();
        var placed = new List<string[]>();
        int total = 0;
        foreach (var lr in WorldReader.Levels(provider))
        {
            if (only.Count > 0 && !only.Contains(lr.Name)) continue;
            if (WorldReader.SkipLevels.Contains(lr.Name)) continue;
            var pkg = provider.LoadPackage(lr.PackagePath);
            var exports = pkg.GetExports().ToList();
            var level = exports.OfType<ULevel>().FirstOrDefault();
            if (level == null) continue;
            var levelXf = Matrix4x4.CreateTranslation(lr.Offset.X, lr.Offset.Y, lr.Offset.Z);
            var cache = new Dictionary<UObject, Matrix4x4>();
            var w = new GlbWriter(texStore);
            int n = 0;
            foreach (var ai in level.Actors)
            {
                UObject? actor;
                try { actor = ai.IsNull ? null : ai.Load(); } catch { continue; }
                if (actor == null) continue;
                var anc = Ue.Ancestry(actor.Class);
                if (!anc.Any(WildBases.Contains)) continue;
                if (Ue.Bool(actor, "bHidden", false)) continue;
                var root = Ue.Obj(actor, "RootComponent");
                if (root == null) continue;
                var rootW = Ue.World(root, cache, levelXf);
                // The actor's skeletal mesh components (instances exported in this level, with the blueprint as template).
                var comps = exports.Where(e => e.Outer == actor && e.ExportType.Contains("SkeletalMeshComponent")).ToList();
                int added = 0;
                foreach (var comp in comps)
                {
                    if (!Ue.Bool(comp, "bVisible", true) || Ue.Bool(comp, "bHiddenInGame", false)) continue;
                    var mesh = Ue.Obj(comp, "SkeletalMesh") as USkeletalMesh;
                    if (mesh == null) continue;
                    UAnimSequence? anim = null;
                    if (WildIdle.TryGetValue(mesh.Name, out var ap))
                    {
                        if (!animByPath.TryGetValue(ap, out anim))
                        {
                            try { anim = provider.LoadPackage(ap).GetExports().OfType<UAnimSequence>().FirstOrDefault(); } catch (Exception e) { log.Add($"{ap}: {e.Message}"); }
                            animByPath[ap] = anim;
                        }
                    }
                    else log.Add($"{lr.Name} {actor.Name}: no idle anim for {mesh.Name}, rest pose");
                    var key = mesh.GetPathName() + "|" + (anim?.GetPathName() ?? "rest");
                    if (!posed.TryGetValue(key, out var md))
                    {
                        try { md = Pose(mesh, anim, false, key, log); }
                        catch (Exception e) { log.Add($"{key}: {e.GetType().Name} {e.Message}"); md = null; }
                        posed[key] = md;
                    }
                    if (md == null) continue;
                    var infos = MatInfos(comp, mesh, md, mats);
                    // Mesh relative to the actor's root (critter blueprints attach the mesh directly to the root).
                    var world = comp == root ? rootW : Ue.Relative(comp) * rootW;
                    w.AddInstance(md, infos, world, actor.Name + "|" + comp.Name + "|" + mesh.Name);
                    added++;
                    var tkey = key + "|" + string.Join(",", infos.Select(i => i.Key));
                    if (testSeen.Add(tkey))
                    {
                        int k = testSeen.Count - 1;
                        testW.AddInstance(md, infos, Matrix4x4.CreateTranslation(k * 150f, 0, 0), (anim?.Name ?? "rest") + "|" + actor.Class?.Name);
                    }
                }
                if (added == 0) { log.Add($"{lr.Name} {actor.Name} ({actor.Class?.Name}): no skeletal mesh"); continue; }
                placed.Add(new[] { lr.Name, actor.Name, actor.Class?.Name ?? "" });
                n++;
            }
            if (n == 0) continue;
            w.Save(Path.Combine(dir, lr.Name + ".glb"));
            index[lr.Name] = new Dictionary<string, object> { ["count"] = n, ["tris"] = w.Tris };
            total += n;
            Console.WriteLine($"{lr.Name,-28} wildlife={n} tris={w.Tris}");
        }
        testW.Save(Path.Combine(outDir, "wild_test.glb"));
        File.WriteAllText(Path.Combine(dir, "index.json"), Newtonsoft.Json.JsonConvert.SerializeObject(new Dictionary<string, object> { ["levels"] = index, ["placed"] = placed }, Newtonsoft.Json.Formatting.Indented));
        File.WriteAllLines(Path.Combine(outDir, "wildlife_log.txt"), log);
        Console.WriteLine($"wildlife: {total}, posed meshes: {posed.Count(p => p.Value != null)}, test combos: {testSeen.Count}, log lines: {log.Count}");
    }

    // NPCs: the actor's skeletal mesh posed with its IdleAnim (frame 0; else the skeleton's best-matching idle), plus the
    // static meshes and skeletal meshes attached to its sockets (helmets, sheaths, tools, the chef riding the crow).
    // Every part of one NPC is written with node names starting "<actor>|", so the map culls them as one model.
    public static void RunNpcs(DefaultFileProvider provider, string outDir, HashSet<string> only)
    {
        var dir = Path.Combine(outDir, "npc");
        Directory.CreateDirectory(dir);
        var texStore = new TextureStore(Path.Combine(outDir, "tex"));
        var mats = new MaterialColors();
        var meshCache = new MeshCache();
        var log = new List<string>();
        var idles = Enemies.IdleIndex(provider, log);
        var testW = new GlbWriter(texStore);
        var index = new Dictionary<string, object>();
        var placed = new List<string[]>();
        int total = 0;
        foreach (var lr in WorldReader.Levels(provider))
        {
            if (only.Count > 0 && !only.Contains(lr.Name)) continue;
            if (WorldReader.SkipLevels.Contains(lr.Name)) continue;
            var exports = provider.LoadPackage(lr.PackagePath).GetExports().ToList();
            var level = exports.OfType<ULevel>().FirstOrDefault();
            if (level == null) continue;
            var levelXf = Matrix4x4.CreateTranslation(lr.Offset.X, lr.Offset.Y, lr.Offset.Z);
            var cache = new Dictionary<UObject, Matrix4x4>();
            var w = new GlbWriter(texStore);
            int n = 0;
            foreach (var ai in level.Actors)
            {
                UObject? actor;
                try { actor = ai.IsNull ? null : ai.Load(); } catch { continue; }
                if (actor == null) continue;
                var anc = Ue.Ancestry(actor.Class);
                if (!anc.Contains("NPC_Base_C") && actor.Class?.Name != "Shopkeeper_C") continue;
                if (Ue.Bool(actor, "bHidden", false)) continue;
                var comps = exports.Where(e => e.Outer == actor).ToList();
                bool Visible(UObject c) => Ue.Bool(c, "bVisible", true) && !Ue.Bool(c, "bHiddenInGame", false)
                    && !System.Text.RegularExpressions.Regex.IsMatch(c.Name, "FX|Test|Blocker|Collision", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                var posedBones = new Dictionary<UObject, ((string[] Names, Matrix4x4[] Pose) Bones, USkeletalMesh Mesh, Matrix4x4 World)>();
                int parts = 0;
                // Skeletal meshes first, parents before children (a socket needs its parent's posed bones).
                var skels = comps.Where(c => c.ExportType.Contains("SkeletalMeshComponent")).OrderBy(c => Ue.Obj(c, "AttachParent")?.ExportType.Contains("Skeletal") == true ? 1 : 0).ToList();
                var statics = comps.Where(c => c.ExportType.Contains("StaticMeshComponent")).ToList();
                foreach (var comp in skels.Concat(statics))
                {
                    if (!Visible(comp)) continue;
                    Matrix4x4 world;
                    var parentComp = Ue.Obj(comp, "AttachParent");
                    var socket = Ue.Val(comp, "AttachSocketName")?.ToString();
                    if (parentComp != null && !string.IsNullOrEmpty(socket) && socket != "None" && posedBones.TryGetValue(parentComp, out var pb))
                    {
                        var sm = SocketMatrix(pb.Mesh, pb.Bones, socket);
                        if (sm == null) { log.Add($"{lr.Name} {actor.Name}.{comp.Name}: socket {socket} not found"); continue; }
                        world = Ue.Relative(comp) * sm.Value * pb.World;
                    }
                    else world = Ue.World(comp, cache, levelXf);
                    if (comp.ExportType.Contains("Skeletal"))
                    {
                        var mesh = Ue.Obj(comp, "SkeletalMesh") as USkeletalMesh;
                        if (mesh == null) continue;
                        // The main mesh plays the actor's IdleAnim; other skeletal parts their own AnimToPlay, else an idle for their skeleton.
                        UAnimSequence? anim = null;
                        if (Ue.Val(comp, "AnimationData") is FScriptStruct ss && ss.StructType is FStructFallback fb)
                            foreach (var p in fb.Properties) if (p.Name.Text == "AnimToPlay") anim = Ue.LoadRef(p.Tag?.GenericValue) as UAnimSequence;
                        bool main = parentComp == null || !parentComp.ExportType.Contains("Skeletal");
                        if (anim == null && main) anim = Ue.Obj(actor, "IdleAnim") as UAnimSequence;
                        if (anim != null && anim.Skeleton?.ResolvedObject?.GetPathName() != mesh.Skeleton?.ResolvedObject?.GetPathName()) anim = null;
                        anim ??= Enemies.PickIdle(idles, mesh, actor.Class?.Name + " " + comp.Name);
                        var md = Pose(mesh, anim, false, mesh.GetPathName() + "|" + (anim?.GetPathName() ?? "rest"), log, out var bones);
                        if (md == null || bones == null) continue;
                        posedBones[comp] = (bones.Value, mesh, world);
                        var infos = MatInfos(comp, mesh, md, mats);
                        w.AddInstance(md, infos, world, actor.Name + "|" + comp.Name + "|" + (anim?.Name ?? "rest"));
                        log.Add($"{lr.Name} {actor.Name} ({actor.Class?.Name}).{comp.Name}: {mesh.Name} posed with {anim?.Name ?? "rest pose"}");
                        parts++;
                    }
                    else
                    {
                        var sm = WorldReader.LoadMesh(comp);
                        var md = sm == null ? null : meshCache.Get(sm);
                        if (md == null) continue;
                        var over = new List<UObject?>();
                        if (Ue.Val(comp, "OverrideMaterials") is UScriptArray oa) over = oa.Properties.Select(p => Ue.LoadRef(p.GenericValue)).ToList();
                        int slots = Math.Max(md.Mats.Count, md.Sections.Count == 0 ? 0 : md.Sections.Max(s => s.MatSlot) + 1);
                        var infos = new List<MaterialColors.MatInfo>();
                        for (int i = 0; i < slots; i++) infos.Add(mats.Get(i < over.Count && over[i] != null ? over[i] : (i < md.Mats.Count ? md.Mats[i] : null)));
                        w.AddInstance(md, infos, world, actor.Name + "|" + comp.Name);
                        log.Add($"{lr.Name} {actor.Name}.{comp.Name}: {sm!.Name}" + (string.IsNullOrEmpty(socket) || socket == "None" ? "" : $" on socket {socket}"));
                        parts++;
                    }
                }
                if (parts == 0) { log.Add($"{lr.Name} {actor.Name} ({actor.Class?.Name}): nothing visible"); continue; }
                placed.Add(new[] { lr.Name, actor.Name, actor.Class?.Name ?? "" });
                n++;
            }
            if (n == 0) continue;
            w.Save(Path.Combine(dir, lr.Name + ".glb"));
            index[lr.Name] = new Dictionary<string, object> { ["count"] = n, ["tris"] = w.Tris };
            total += n;
            Console.WriteLine($"{lr.Name,-28} npcs={n} tris={w.Tris}");
        }
        File.WriteAllText(Path.Combine(dir, "index.json"), Newtonsoft.Json.JsonConvert.SerializeObject(new Dictionary<string, object> { ["levels"] = index, ["placed"] = placed }, Newtonsoft.Json.Formatting.Indented));
        File.WriteAllLines(Path.Combine(outDir, "npcs_log.txt"), log);
        Console.WriteLine($"npcs: {total}, log lines: {log.Count}");
    }

    // A socket's transform in mesh space under the given pose: the socket on the mesh or its skeleton, else a bone of that name.
    static Matrix4x4? SocketMatrix(USkeletalMesh mesh, (string[] Names, Matrix4x4[] Pose) bones, string socket)
    {
        IEnumerable<FPackageIndex> all = mesh.Sockets ?? Array.Empty<FPackageIndex>();
        try { if (mesh.Skeleton?.Load<USkeleton>() is { } sk && sk.Sockets != null) all = all.Concat(sk.Sockets); } catch { }
        foreach (var pi in all)
        {
            USkeletalMeshSocket? s;
            try { s = pi.Load<USkeletalMeshSocket>(); } catch { continue; }
            if (s == null || !s.SocketName.Text.Equals(socket, StringComparison.OrdinalIgnoreCase)) continue;
            int b = Array.FindIndex(bones.Names, x => x.Equals(s.BoneName.Text, StringComparison.OrdinalIgnoreCase));
            if (b < 0) return null;
            return Ue.Srt(s.RelativeScale, s.RelativeRotation, s.RelativeLocation) * bones.Pose[b];
        }
        int bi = Array.FindIndex(bones.Names, x => x.Equals(socket, StringComparison.OrdinalIgnoreCase));
        return bi >= 0 ? bones.Pose[bi] : null;
    }

    static List<MaterialColors.MatInfo> MatInfos(UObject comp, USkeletalMesh mesh, MeshData md, MaterialColors mats)
    {
        var overrides = new List<UObject?>();
        if (Ue.Val(comp, "OverrideMaterials") is UScriptArray oa) overrides = oa.Properties.Select(p => Ue.LoadRef(p.GenericValue)).ToList();
        int slots = Math.Max(md.Mats.Count, md.Sections.Count == 0 ? 0 : md.Sections.Max(s => s.MatSlot) + 1);
        var infos = new List<MaterialColors.MatInfo>();
        for (int i = 0; i < slots; i++)
        {
            var m = i < overrides.Count && overrides[i] != null ? overrides[i] : (i < md.Mats.Count ? md.Mats[i] : null);
            infos.Add(mats.Get(m) with { Skip = false, A = 1 });
        }
        return infos;
    }

    // UE FTransform -> row-vector matrix (v' = v * M): scale, rotate, translate.
    static Matrix4x4 M(FQuat q, FVector t, FVector s) =>
        Matrix4x4.CreateScale(s.X, s.Y, s.Z) * Matrix4x4.CreateFromQuaternion(new Quaternion(q.X, q.Y, q.Z, q.W)) * Matrix4x4.CreateTranslation(t.X, t.Y, t.Z);

    static readonly Dictionary<string, CAnimSequence?> animCache = new();

    internal static MeshData? Pose(USkeletalMesh mesh, UAnimSequence? anim, bool hideHead, string key, List<string> log)
        => Pose(mesh, anim, hideHead, key, log, out _);

    // bones: each bone's posed transform in mesh space (UE row-vector matrices), for placing socket attachments.
    internal static MeshData? Pose(USkeletalMesh mesh, UAnimSequence? anim, bool hideHead, string key, List<string> log, out (string[] Names, Matrix4x4[] Pose)? bones)
    {
        bones = null;
        if (!mesh.TryConvert(out var cm) || cm.LODs.Count == 0) return null;
        var lod = cm.LODs[0];
        var rs = mesh.ReferenceSkeleton;
        int nb = rs.FinalRefBoneInfo.Length;
        var parent = rs.FinalRefBoneInfo.Select(b => b.ParentIndex).ToArray();
        var names = rs.FinalRefBoneInfo.Select(b => b.Name.Text).ToArray();
        var refPose = rs.FinalRefBonePose;
        var bind = new Matrix4x4[nb];
        for (int i = 0; i < nb; i++)
        {
            var l = M(refPose[i].Rotation, refPose[i].Translation, refPose[i].Scale3D);
            bind[i] = parent[i] >= 0 ? l * bind[parent[i]] : l;
        }

        // Local pose: reference pose, replaced by the animation's frame 0 wherever it has keys.
        var lq = refPose.Select(t => t.Rotation).ToArray();
        var lt = refPose.Select(t => t.Translation).ToArray();
        var ls = refPose.Select(t => t.Scale3D).ToArray();
        if (anim != null)
        {
            var apath = anim.GetPathName();
            if (!animCache.TryGetValue(apath, out var seq))
            {
                var skel = anim.Skeleton?.Load<USkeleton>() ?? mesh.Skeleton.Load<USkeleton>();
                seq = skel == null ? null : skel.ConvertAnims(anim).Sequences.FirstOrDefault();
                animCache[apath] = seq;
            }
            if (seq == null) { log.Add($"{key}: anim did not convert"); return null; }
            var skelR = (anim.Skeleton?.Load<USkeleton>() ?? mesh.Skeleton.Load<USkeleton>())!.ReferenceSkeleton;
            int matched = 0, keyed = 0;
            // How much the pose moves over its length (still poses should not move at all).
            float maxAngle = 0;
            foreach (var tr in seq.Tracks)
            {
                if (tr == null || !tr.HasKeys() || seq.NumFrames < 2) continue;
                FQuat q0 = new(0, 0, 0, 1), q1 = new(0, 0, 0, 1); FVector t0 = new(0, 0, 0), t1 = new(0, 0, 0), s0 = new(1, 1, 1), s1 = new(1, 1, 1);
                tr.GetBoneTransform(0, seq.NumFrames, ref q0, ref t0, ref s0);
                tr.GetBoneTransform(seq.NumFrames - 1, seq.NumFrames, ref q1, ref t1, ref s1);
                float d = Math.Abs(q0.X * q1.X + q0.Y * q1.Y + q0.Z * q1.Z + q0.W * q1.W);
                maxAngle = Math.Max(maxAngle, 2 * MathF.Acos(Math.Min(1, d)) * 180 / MathF.PI);
            }
            log.Add($"{key}: maxBoneTurnOverAnim={maxAngle:F1}deg");
            for (int j = 0; j < seq.Tracks.Count && j < skelR.FinalRefBoneInfo.Length; j++)
            {
                var tr = seq.Tracks[j];
                if (tr == null || !tr.HasKeys()) continue;
                keyed++;
                if (!rs.FinalNameToIndexMap.TryGetValue(skelR.FinalRefBoneInfo[j].Name.Text, out var bi))
                {
                    var nm = skelR.FinalRefBoneInfo[j].Name.Text;
                    bi = Array.FindIndex(names, x => string.Equals(x, nm, StringComparison.OrdinalIgnoreCase));
                    if (bi < 0) continue;
                }
                matched++;
                FQuat q = lq[bi]; FVector t = lt[bi]; FVector s = ls[bi];
                tr.GetBoneTransform(0, seq.NumFrames, ref q, ref t, ref s);
                lq[bi] = q; lt[bi] = t; ls[bi] = s;
            }
            log.Add($"{key}: frames={seq.NumFrames} keyedTracks={keyed} matched={matched} meshBones={nb}");
        }
        var pose = new Matrix4x4[nb];
        var skin = new Matrix4x4[nb];
        for (int i = 0; i < nb; i++)
        {
            var l = M(lq[i], lt[i], ls[i]);
            pose[i] = parent[i] >= 0 ? l * pose[parent[i]] : l;
            Matrix4x4.Invert(bind[i], out var inv);
            skin[i] = inv * pose[i];
        }
        bones = (names, pose);

        // Bones hidden with the head (RelicHead variants put a relic where the skull was).
        var hidden = new bool[nb];
        if (hideHead)
        {
            int head = Array.FindIndex(names, x => x.Equals("head", StringComparison.OrdinalIgnoreCase));
            for (int i = 0; i < nb; i++)
                for (int b = i; b >= 0; b = parent[b])
                    if (b == head) { hidden[i] = true; break; }
        }

        var verts = lod.Verts;
        var md = new MeshData { Path = key };
        md.Pos = new float[verts.Length * 3];
        md.Uv = new float[verts.Length * 2];
        var vHidden = new bool[verts.Length];
        for (int i = 0; i < verts.Length; i++)
        {
            var v = new Vector3(verts[i].Position.X, verts[i].Position.Y, verts[i].Position.Z);
            var acc = Vector3.Zero; float wsum = 0, hw = 0;
            foreach (var inf in verts[i].Influences)
            {
                if (inf.Weight <= 0 || inf.Bone < 0 || inf.Bone >= nb) continue;
                acc += Vector3.Transform(v, skin[inf.Bone]) * inf.Weight;
                wsum += inf.Weight;
                if (hidden[inf.Bone]) hw += inf.Weight;
            }
            var p = wsum > 0 ? acc / wsum : v;
            vHidden[i] = wsum > 0 && hw / wsum > 0.5f;
            md.Pos[i * 3] = p.X; md.Pos[i * 3 + 1] = p.Z; md.Pos[i * 3 + 2] = p.Y;
            md.Uv[i * 2] = verts[i].UV.U; md.Uv[i * 2 + 1] = verts[i].UV.V;
        }
        var ib = lod.Indices.Value;
        foreach (var s in lod.Sections.Value)
        {
            var idx = new List<uint>(s.NumFaces * 3);
            for (int t = 0; t < s.NumFaces; t++)
            {
                int b = s.FirstIndex + t * 3;
                uint a0 = (uint)ib[b], a1 = (uint)ib[b + 2], a2 = (uint)ib[b + 1];
                if (vHidden[a0] || vHidden[a1] || vHidden[a2]) continue;
                idx.Add(a0); idx.Add(a1); idx.Add(a2);
            }
            md.Sections.Add((idx.ToArray(), s.MaterialIndex));
            md.Tris += idx.Count / 3;
        }
        try { if (mesh.Materials != null) foreach (var m in mesh.Materials) md.Mats.Add(m?.Load()); } catch { }
        try { if (md.Mats.Count == 0) foreach (var m in mesh.SkeletalMaterials ?? Array.Empty<FSkeletalMaterial>()) md.Mats.Add(m.Material?.Load()); } catch { }
        return md;
    }

    // Checks that skinning data and bone transforms agree: each bone's bind position should sit inside the
    // vertices it mostly drives, and anim tracks should use the same quaternion convention as the reference pose.
    public static void Probe(DefaultFileProvider provider, string meshPath, string animPath)
    {
        var mesh = provider.LoadPackage(meshPath.Split('.')[0]).GetExports().OfType<USkeletalMesh>().First();
        mesh.TryConvert(out var cm);
        var rs = mesh.ReferenceSkeleton;
        int nb = rs.FinalRefBoneInfo.Length;
        var bind = new Matrix4x4[nb];
        for (int i = 0; i < nb; i++)
        {
            var t = rs.FinalRefBonePose[i];
            var l = M(t.Rotation, t.Translation, t.Scale3D);
            var pi = rs.FinalRefBoneInfo[i].ParentIndex;
            bind[i] = pi >= 0 ? l * bind[pi] : l;
        }
        var sum = new Vector3[nb]; var cnt = new int[nb];
        foreach (var v in cm!.LODs[0].Verts)
        {
            var top = v.Influences.OrderByDescending(x => x.Weight).FirstOrDefault();
            if (top == null || top.Weight < 0.9f) continue;
            sum[top.Bone] += new Vector3(v.Position.X, v.Position.Y, v.Position.Z); cnt[top.Bone]++;
        }
        Console.WriteLine($"bones={nb} verts={cm.LODs[0].Verts.Length}");
        for (int i = 0; i < nb; i++)
        {
            if (cnt[i] < 20) continue;
            var c = sum[i] / cnt[i];
            var b = bind[i].Translation;
            Console.WriteLine($"{rs.FinalRefBoneInfo[i].Name.Text,-20} n={cnt[i],5} bone=({b.X,7:F1},{b.Y,7:F1},{b.Z,7:F1}) centroid=({c.X,7:F1},{c.Y,7:F1},{c.Z,7:F1}) dist={(c - b).Length(),6:F1}");
        }
        var anim = provider.LoadPackage(animPath.Split('.')[0]).GetExports().OfType<UAnimSequence>().First();
        var skel = anim.Skeleton.Load<USkeleton>()!;
        Console.WriteLine($"codec={anim.BoneCompressionCodec?.GetType().FullName ?? "null"} data={anim.CompressedDataStructure?.GetType().FullName ?? "null"} settings={anim.BoneCompressionSettings?.GetPathName()}");
        try { var bcs = anim.BoneCompressionSettings?.Load(); Console.WriteLine($"settings loaded: {bcs?.GetType().FullName} {bcs?.ExportType}"); if (bcs != null) foreach (var p in bcs.Properties) Console.WriteLine($"  {p.Name.Text} = {Ue.Simplify(p.Tag?.GenericValue)}"); }
        catch (Exception e) { Console.WriteLine("settings load failed: " + e.Message); }
        var seq = skel.ConvertAnims(anim).Sequences[0];
        Console.WriteLine($"anim {anim.Name} frames={seq.NumFrames} tracks={seq.Tracks.Count} skelBones={skel.ReferenceSkeleton.FinalRefBoneInfo.Length}");
        for (int j = 0; j < seq.Tracks.Count; j++)
        {
            var nm = skel.ReferenceSkeleton.FinalRefBoneInfo[j].Name.Text;
            if (!rs.FinalNameToIndexMap.TryGetValue(nm, out var bi)) continue;
            var r = rs.FinalRefBonePose[bi];
            FQuat q = r.Rotation; FVector t = r.Translation; FVector s = r.Scale3D;
            if (!seq.Tracks[j].HasKeys()) continue;
            seq.Tracks[j].GetBoneTransform(0, seq.NumFrames, ref q, ref t, ref s);
            float dot = q.X * r.Rotation.X + q.Y * r.Rotation.Y + q.Z * r.Rotation.Z + q.W * r.Rotation.W;
            float dotConj = -q.X * r.Rotation.X - q.Y * r.Rotation.Y - q.Z * r.Rotation.Z + q.W * r.Rotation.W;
            Console.WriteLine($"  {nm,-20} |dot|={Math.Abs(dot):F3} |dotConj|={Math.Abs(dotConj):F3} dT={(new Vector3(t.X - r.Translation.X, t.Y - r.Translation.Y, t.Z - r.Translation.Z)).Length():F2} t=({t.X:F1},{t.Y:F1},{t.Z:F1}) s=({s.X:F2},{s.Y:F2},{s.Z:F2}) q=({q.X:F2},{q.Y:F2},{q.Z:F2},{q.W:F2}) keys q{seq.Tracks[j].KeyQuat.Length} p{seq.Tracks[j].KeyPos.Length} s{seq.Tracks[j].KeyScale.Length}");
        }
    }
}
