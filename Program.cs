using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Versions;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.Encryption.Aes;

// The game's Paks folder. build.py finds it through Steam and sets QD_PAKS; set it yourself to run the extractor directly.
var GameDir = Environment.GetEnvironmentVariable("QD_PAKS") ?? "";
if (!Directory.Exists(GameDir) || !Directory.EnumerateFiles(GameDir, "*.pak").Any())
{
    Console.Error.WriteLine($"QD_PAKS must point to the game's Paks folder (...\\QueensDomain\\QueensDomain\\Content\\Paks). Got: '{GameDir}'");
    Environment.Exit(1);
}

var provider = new DefaultFileProvider(GameDir, SearchOption.TopDirectoryOnly, true, new VersionContainer(EGame.GAME_UE4_27));
provider.Initialize();
provider.SubmitKey(new FGuid(), new FAesKey(new byte[32]));
// Plugin content (/ACLPlugin/... holds the animation compression settings the still poses need).
provider.LoadVirtualPaths();
try { provider.LoadLocalization(ELanguage.English); } catch (Exception e) { Console.Error.WriteLine("loc: " + e.Message); }

var cmd = args.Length > 0 ? args[0] : "list";
var outDir = args.Length > 1 ? args[1] : ".";
Directory.CreateDirectory(outDir);

switch (cmd)
{
    case "list":
        File.WriteAllLines(Path.Combine(outDir, "files.txt"), provider.Files.Keys.OrderBy(k => k));
        Console.WriteLine($"{provider.Files.Count} files");
        break;
    case "entities":
    {
        var levels = WorldReader.Levels(provider);
        var all = new Dictionary<string, object?>();
        var stats = new List<string>();
        var classDefaults = new Dictionary<string, object?>();
        bool wantMesh = args.Length > 2 && args[2] == "mesh";
        foreach (var lr in levels)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var ld = WorldReader.Read(provider, lr, wantMesh);
            foreach (var kv in ld.ClassDefaults) classDefaults[kv.Key] = kv.Value;
            var uniq = ld.Meshes.Select(m => m.Mesh).Distinct().ToList();
            long tris0 = 0, trisL = 0, trisU0 = 0, trisU1 = 0;
            foreach (var m in ld.Meshes)
            {
                var lods = m.Mesh.RenderData?.LODs;
                if (lods == null || lods.Length == 0) continue;
                tris0 += lods[0].Sections.Sum(s => (long)s.NumTriangles);
                trisL += lods[^1].Sections.Sum(s => (long)s.NumTriangles);
            }
            foreach (var u in uniq)
            {
                var lods = u.RenderData?.LODs;
                if (lods == null || lods.Length == 0) continue;
                trisU0 += lods[0].Sections.Sum(s => (long)s.NumTriangles);
                trisU1 += lods[Math.Min(1, lods.Length - 1)].Sections.Sum(s => (long)s.NumTriangles);
            }
            var line = $"{lr.Name,-28} off={lr.Offset} actors={ld.Actors.Count} smInst={ld.Meshes.Count} uniq={uniq.Count} tris0={tris0} trisLast={trisL} uniqTris0={trisU0} uniqTris1={trisU1} {sw.ElapsedMilliseconds}ms";
            Console.WriteLine(line); stats.Add(line);
            all[lr.Name] = new Dictionary<string, object?> { ["package"] = lr.PackagePath, ["offset"] = new[] { lr.Offset.X, lr.Offset.Y, lr.Offset.Z }, ["initiallyLoaded"] = lr.InitiallyLoaded, ["actors"] = ld.Actors };
        }
        File.WriteAllText(Path.Combine(outDir, "entities_raw.json"), Newtonsoft.Json.JsonConvert.SerializeObject(all));
        if (wantMesh) File.WriteAllLines(Path.Combine(outDir, "geomstats.txt"), stats);
        File.WriteAllText(Path.Combine(outDir, "class_defaults.json"), Newtonsoft.Json.JsonConvert.SerializeObject(classDefaults, Newtonsoft.Json.Formatting.Indented));
        break;
    }
    case "geometry":
    {
        var glbDir = Path.Combine(outDir, "glb");
        Directory.CreateDirectory(glbDir);
        var only = args.Skip(2).ToHashSet();
        var mats = new MaterialColors();
        var meshCache = new MeshCache();
        var texStore = new TextureStore(Path.Combine(outDir, "tex"));
        var summary = new List<string>();
        // The Tower of Knowledge's flesh walls (every Necro_Deco_Base actor: the walls, their lumps and the boss door)
        // dissolve once the necro heads are destroyed. They go to their own file so the map can toggle them.
        var flesh = new GlbWriter(texStore);
        var fleshLevels = new HashSet<string> { "300_LIB", "320_LIB_basement" };
        var ancCache = new Dictionary<string, bool>();
        bool IsFlesh(UObject? actor) => actor?.Class is { } c && (ancCache.TryGetValue(c.Name, out var f) ? f : ancCache[c.Name] = Ue.Ancestry(c).Contains("Necro_Deco_Base_C"));
        foreach (var lr in WorldReader.Levels(provider))
        {
            if (only.Count > 0 && !only.Contains(lr.Name)) continue;
            if (WorldReader.SkipLevels.Contains(lr.Name)) continue;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var ld = WorldReader.Read(provider, lr, true);
            var w0 = new GlbWriter(texStore);
            var fxActor = new System.Text.RegularExpressions.Regex(@"FX|LightBeam|FogCard|FogLight|Dust|Drips|Splash|Glow|GodRay|Bubbles|Smoke|Steam|Particle|Leaves|Puffer|Visualizer", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            foreach (var mi in ld.Meshes)
            {
                var md = meshCache.Get(mi.Mesh);
                if (md == null) continue;
                var acls = mi.Actor?.Class?.Name ?? "";
                var w = fleshLevels.Contains(lr.Name) && IsFlesh(mi.Actor) ? flesh : w0;
                if (fxActor.IsMatch(acls)) { w.Skipped++; continue; }
                if (mi.Spline != null) md = SplineDeform.Apply(md, mi.Spline);
                int slots = Math.Max(md.Mats.Count, md.Sections.Count == 0 ? 0 : md.Sections.Max(s => s.MatSlot) + 1);
                var infos = new List<MaterialColors.MatInfo>();
                for (int i = 0; i < slots; i++)
                {
                    var m = i < mi.OverrideMaterials.Count && mi.OverrideMaterials[i] != null ? mi.OverrideMaterials[i] : (i < md.Mats.Count ? md.Mats[i] : null);
                    infos.Add(mats.Get(m));
                }
                if (MeshClip.KeepBoxes.TryGetValue((lr.Name, mi.Mesh.Name), out var box))
                {
                    var clipped = MeshClip.ToBox(md, mi.World, box.Lo, box.Hi);
                    Console.WriteLine($"  clipped {mi.Mesh.Name}: {md.Tris} -> {clipped?.Tris ?? 0} triangles");
                    if (clipped != null) w.AddInstance(clipped, infos, System.Numerics.Matrix4x4.Identity, mi.Actor?.Name ?? "");
                    continue;
                }
                w.AddInstance(md, infos, mi.World, mi.Actor?.Name ?? "");
            }
            w0.Save(Path.Combine(glbDir, lr.Name + ".glb"));
            var line = $"{lr.Name,-28} nodes={w0.NodeCount} skipped={w0.Skipped} tris={w0.Tris} {sw.ElapsedMilliseconds}ms";
            Console.WriteLine(line); summary.Add(line);
        }
        if (flesh.NodeCount > 0)
        {
            // Run with both flesh levels (or all levels); a run with only one of them writes only that level's flesh.
            flesh.Save(Path.Combine(glbDir, "300_LIB_flesh.glb"));
            var line = $"{"300_LIB_flesh",-28} nodes={flesh.NodeCount} tris={flesh.Tris}";
            Console.WriteLine(line); summary.Add(line);
        }
        summary.Add($"textures: {texStore.Count}");
        File.WriteAllLines(Path.Combine(outDir, "materials_log.tsv"), mats.Log.Prepend("material\tblend\ttexture\ttransparent\tcandidates (param=texture:score)"));
        Console.WriteLine(summary[^1]);
        File.WriteAllLines(Path.Combine(outDir, "geometry_summary.txt"), summary);
        break;
    }
    case "catalog":
        Catalog.Run(provider, outDir);
        break;
    case "enemies":
        Enemies.Run(provider, outDir);
        break;
    case "skeletons":
        Skeletons.Run(provider, outDir, args.Skip(2).ToHashSet());
        break;
    case "wildlife":
        Skeletons.RunWildlife(provider, outDir, args.Skip(2).ToHashSet());
        break;
    case "npcs":
        Skeletons.RunNpcs(provider, outDir, args.Skip(2).ToHashSet());
        break;
    case "skelprobe":
        Skeletons.Probe(provider, args[2], args[3]);
        break;
    case "ismcheck":
    {
        var mats = new MaterialColors(); var meshCache = new MeshCache();
        foreach (var lr in WorldReader.Levels(provider).Where(l => args.Skip(2).Contains(l.Name)))
        {
            var ld = WorldReader.Read(provider, lr, true);
            foreach (var g in ld.Meshes.GroupBy(m => m.Mesh.GetPathName()).OrderByDescending(g => g.Count()).Take(15))
            {
                var mi = g.First(); var md = meshCache.Get(mi.Mesh);
                var infos = md == null ? "NULL-MESHDATA" : string.Join(",", md.Mats.Select(m => { var x = mats.Get(m); return (m?.Name ?? "null") + (x.Skip ? "[SKIP]" : "") + (x.A < 1 ? "[T]" : ""); }));
                Console.WriteLine($"{lr.Name} {g.Count(),6} {g.Key.Split('.').Last()} comp={mi.Component.ExportType} actor={mi.Actor?.Name} mats={infos}");
            }
        }
        break;
    }
    case "vcprobe":
    {
        // vcprobe <outDir> <level> <mesh name>: vertex colors and UV channels per section of a level's mesh.
        var mats = new MaterialColors();
        var lr = WorldReader.Levels(provider).First(l => l.Name == args[2]);
        var ld = WorldReader.Read(provider, lr, true);
        var mi = ld.Meshes.First(m => m.Mesh.Name == args[3]);
        var lod = mi.Mesh.RenderData!.LODs[0];
        var cols = lod.ColorVertexBuffer?.Data;
        Console.WriteLine($"verts={lod.PositionVertexBuffer!.Verts.Length} uvChannels={lod.VertexBuffer?.NumTexCoords} colors={cols?.Length ?? 0} comp={mi.Component.ExportType} lodData={Ue.FindTag(mi.Component, "LODData") != null}");
        var ib = lod.IndexBuffer!;
        foreach (var sec in lod.Sections)
        {
            var mat = sec.MaterialIndex < mi.Mesh.StaticMaterials!.Length ? mi.Mesh.StaticMaterials[sec.MaterialIndex].MaterialInterface?.Load() : null;
            var hist = new int[4, 5];
            var vs = new HashSet<uint>();
            for (int t = 0; t < sec.NumTriangles * 3; t++) vs.Add((uint)ib[sec.FirstIndex + t]);
            foreach (var v in vs)
            {
                if (cols == null) break;
                var c = cols[v];
                byte[] ch = { c.R, c.G, c.B, c.A };
                for (int k = 0; k < 4; k++) hist[k, Math.Min(4, ch[k] * 5 / 256)]++;
            }
            string H(int k) => string.Join("/", Enumerable.Range(0, 5).Select(b => hist[k, b]));
            Console.WriteLine($"sec mat={mat?.Name} tris={sec.NumTriangles} verts={vs.Count} R[{H(0)}] G[{H(1)}] B[{H(2)}] A[{H(3)}]");
            if (args.Length > 4 && mat?.Name == args[4] && cols != null)
            {
                var uv = lod.VertexBuffer!.UV;
                var pos = lod.PositionVertexBuffer.Verts;
                var lines = new List<string>();
                for (int t = 0; t < sec.NumTriangles; t++)
                {
                    var row = new List<string>();
                    for (int k = 0; k < 3; k++)
                    {
                        var v = (int)ib[sec.FirstIndex + t * 3 + k];
                        var c = cols[v];
                        row.Add($"{c.R},{c.G},{c.B},{pos[v].X:F0},{pos[v].Y:F0},{pos[v].Z:F0}," + string.Join(",", Enumerable.Range(0, uv[v].UV.Length).Select(ch => $"{uv[v].UV[ch].U:F4},{uv[v].UV[ch].V:F4}")));
                    }
                    lines.Add(string.Join(";", row));
                }
                File.WriteAllLines(Path.Combine(outDir, "vcprobe_tris.txt"), lines);
            }
        }
        break;
    }
    case "json":
        // json <outDir> <pkgPath>...  (paths without extension)
        foreach (var p in args.Skip(2))
        {
            try
            {
                var exports = provider.LoadPackage(p).GetExports();
                var json = Newtonsoft.Json.JsonConvert.SerializeObject(exports, Newtonsoft.Json.Formatting.Indented);
                var outPath = Path.Combine(outDir, p.Replace('/', '_') + ".json");
                File.WriteAllText(outPath, json);
                Console.WriteLine($"{p} -> {json.Length} chars");
            }
            catch (Exception e) { Console.Error.WriteLine($"{p}: {e.Message}"); }
        }
        break;
}
