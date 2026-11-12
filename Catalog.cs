using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse_Conversion.Textures;
using SkiaSharp;

static class Catalog
{
    static readonly string[] Dirs =
    {
        "QueensDomain/Content/Items/", "QueensDomain/Content/Enemies/", "QueensDomain/Content/NPCs/",
        "QueensDomain/Content/Blueprints/", "QueensDomain/Content/Props/", "QueensDomain/Content/RuinsLevel/",
        "QueensDomain/Content/UI/Menus/", "QueensDomain/Content/Player/", "QueensDomain/Content/Artifacts/",
    };

    static readonly System.Text.RegularExpressions.Regex SkipName = new(
        @"(_SM|_SK|_MI|_Mat|_M|_T|_Tex|_TX|_N|_D|_ORM|_Normal|_Diffuse|_Skeleton|_PhysicsAsset|_Anim|_Montage|_BS|_Cue|_SFX|_Seq|_Mask|_Icon_SP)$|^(SM_|SK_|T_|MI_|M_|MF_|A_|NS_|P_|S_)",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    static readonly HashSet<string> NonDataTypes = new()
    {
        "Texture2D", "StaticMesh", "SkeletalMesh", "Material", "MaterialInstanceConstant", "SoundWave", "SoundCue",
        "AnimSequence", "AnimMontage", "Skeleton", "PhysicsAsset", "BlueprintGeneratedClass", "Function", "NiagaraSystem",
        "ParticleSystem", "MaterialFunction", "WidgetBlueprintGeneratedClass", "CurveFloat", "BlendSpace", "LevelSequence",
        "World", "Level", "AnimBlueprintGeneratedClass", "UserDefinedStruct", "UserDefinedEnum", "TextureCube",
    };

    public static void Run(DefaultFileProvider provider, string outDir)
    {
        var iconDir = Path.Combine(outDir, "icons");
        Directory.CreateDirectory(iconDir);
        var assets = new Dictionary<string, object?>();
        var textures = new Dictionary<string, string>(); // texture path -> png file
        var files = provider.Files.Keys.Where(k => k.EndsWith(".uasset") && Dirs.Any(d => k.StartsWith(d)) && !k.Contains("/Maps/"))
            .Where(k => !SkipName.IsMatch(Path.GetFileNameWithoutExtension(k))).OrderBy(k => k).ToList();
        Console.WriteLine($"catalog: scanning {files.Count} packages");
        int n = 0;
        foreach (var f in files)
        {
            if (++n % 500 == 0) Console.WriteLine($"  {n}/{files.Count} ({assets.Count} assets)");
            var pkgPath = f[..^".uasset".Length];
            IEnumerable<UObject> exports;
            try { exports = provider.LoadPackage(pkgPath).GetExports().ToList(); } catch { continue; }
            foreach (var e in exports)
            {
                if (n == 1 && e == exports.First()) Console.WriteLine($"outer of first export: {e.Outer?.GetType().FullName} {e.Outer?.ExportType}");
                bool topLevel = e.Outer == null || e.Outer.GetType().Name.Contains("Package") || e.Outer.ExportType == "Package";
                if (!topLevel || e.Name.StartsWith("Default__") || NonDataTypes.Contains(e.ExportType)) continue;
                List<string> anc;
                try { anc = Ue.Ancestry(e.Class); } catch { continue; }
                if (anc.Contains("Actor") || anc.Contains("ActorComponent") || anc.Contains("UserWidget")) continue;
                var props = Ue.MergedProps(e);
                var key = pkgPath + "." + e.Name;
                var rec = new Dictionary<string, object?> { ["type"] = e.ExportType, ["anc"] = anc, ["props"] = props };
                // icons / images
                foreach (var iconProp in new[] { "ItemIcon", "Image", "Icon", "BestiaryImage", "Thumbnail" })
                {
                    if (Ue.Obj(e, iconProp) is UTexture2D tex)
                    {
                        var tp = tex.GetPathName();
                        if (!textures.TryGetValue(tp, out var png))
                        {
                            png = SaveTexture(tex, iconDir);
                            if (png != null) textures[tp] = png;
                        }
                        if (png != null) rec[iconProp + "_png"] = png;
                    }
                }
                assets[key] = rec;
            }
        }
        File.WriteAllText(Path.Combine(outDir, "catalog_raw.json"), Newtonsoft.Json.JsonConvert.SerializeObject(assets, Newtonsoft.Json.Formatting.Indented));
        Console.WriteLine($"catalog: {assets.Count} assets, {textures.Count} images");
    }

    public static string? SaveTexture(UTexture2D tex, string dir, int maxSize = 256)
    {
        try
        {
            var bmp = tex.Decode();
            if (bmp == null) return null;
            if (bmp.Width > maxSize || bmp.Height > maxSize)
            {
                float s = (float)maxSize / Math.Max(bmp.Width, bmp.Height);
                var resized = bmp.Resize(new SKImageInfo((int)(bmp.Width * s), (int)(bmp.Height * s)), SKFilterQuality.High);
                bmp.Dispose(); bmp = resized;
            }
            var name = tex.Name + ".png";
            using var img = SKImage.FromBitmap(bmp);
            using var data = img.Encode(SKEncodedImageFormat.Png, 90);
            using (var fs = File.Create(Path.Combine(dir, name))) data.SaveTo(fs);
            bmp.Dispose();
            return name;
        }
        catch (Exception ex) { Console.Error.WriteLine($"tex {tex.Name}: {ex.Message}"); return null; }
    }
}
