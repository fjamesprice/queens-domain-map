# Queen's Domain map

An interactive 3D map of [Queen's Domain](https://store.steampowered.com/app/1799840/), built from your own copy of the game. It shows every area's geometry with markers for items, secrets, enemies, NPCs, chests, save points, doors and more, with search, filters and a slice tool for seeing into stacked dungeons.

This repository has no game content in it. The build script reads the game's files from your Steam install (read-only) and writes the map to a folder on your machine, which you then open in a browser. This is a fan project, not affiliated with or endorsed by the game's developers.

## Download (Windows)

1. Download the zip from the latest release on the [Releases page](../../releases/latest) and unzip it anywhere.
2. Double-click `Open map.cmd`.

Nothing else needs installing; the zip brings its own copies of the tools. The first run builds the map from your game files, which takes 10 to 20 minutes and about 1 GB of free space while it runs, then opens the map in your browser. Later runs open it straight away, and rebuild first if the game has been updated. `README.txt` in the zip has the details.

## Building from source

You need:

- Queen's Domain installed through Steam
- [.NET 9 SDK](https://dotnet.microsoft.com/download)
- [Python](https://www.python.org/downloads/) 3.9 or newer
- [Node.js](https://nodejs.org/) 18 or newer (for `gltfpack`, which compresses the models)

Then clone this repository and, from its folder, run:

```
python build.py --serve --open
```

The map is served at http://localhost:8000/. It has to be served over HTTP; opening `index.html` straight from disk won't load the data.

The build takes 10 to 20 minutes. It finds the game through Steam's library list; if it can't, pass the install folder (the one with `QueensDomain.exe`):

```
python build.py --game "D:\SteamLibrary\steamapps\common\QueensDomain"
```

Other options:

| Option | What it does |
|---|---|
| `--out <dir>` | Write the map somewhere other than `./dist` (for example a web server's folder). |
| `--serve-only` | Serve an already built map without rebuilding. |
| `--auto` | Serve the existing map if it was built from the installed game version, otherwise rebuild, then serve. This is what `Open map.cmd` in the release runs. |
| `--open` | Open the map in the default browser once it's served. |
| `--port <n>` | Port to serve on (default 8000; the next free one if that's taken). |
| `--skip-extract` | Reuse the extracted files in `./out` and redo only compression and web data. Useful after editing `web/` or `build_data.py`. |
| `--clean` | Delete `./out` after a successful build. |

Disk use: about 450 MB of intermediate files in `./out` and 130 MB for the finished map. After a game update, run `build.py` again.

Only the Windows build of the game has been tested. Extractor logs go to `out/logs/`.

### Making a release

`python make_release.py <version>` writes `release/queens-domain-map-<version>-windows.zip`. It publishes the extractor as a self-contained `Extractor.exe`, downloads the native gltfpack and the embeddable Python (checked against pinned SHA-256 hashes), and adds the launchers, `README.txt` and `THIRD-PARTY-NOTICES.txt`. Upload the zip with `gh release create v<version> release/queens-domain-map-<version>-windows.zip`.

## How it works

The game is Unreal Engine 4.27 with one unencrypted pak. A C# extractor (`Extractor.csproj`) reads it with [CUE4Parse](https://github.com/FabianFG/CUE4Parse) and writes raw JSON and GLB files to `out/`. `build.py` then compresses the models with gltfpack, copies the viewer from `web/` (three.js, no build step), and runs `build_data.py` to turn the raw dumps into the map's marker and catalog files.

To run a single extractor step by hand, build it with `dotnet build -c Release`, set `QD_PAKS` to the game's `QueensDomain\Content\Paks` folder, and run `dotnet bin/Release/net9.0/Extractor.dll <step> out [args]`.

## Pipeline

| Step | Command | Output (in `out/`) |
|---|---|---|
| Actors in every streamed level of `Ruins_Level_00` | `Extractor entities out` | `entities_raw.json`, `class_defaults.json` |
| Data assets (items, enemies, areas, shops), icons | `Extractor catalog out` | `catalog_raw.json`, `icons/` |
| Level geometry (one GLB per level) and base color textures (`tex/`, shared) | `Extractor geometry out [level...]` | `glb/` |
| Posed skeleton props (one GLB per level), placement list | `Extractor skeletons out [level...]` | `skel/`, `skel/index.json`, `skel_test.glb`, `skeletons_log.txt` |
| Posed wildlife (one GLB per level), placement list | `Extractor wildlife out [level...]` | `wild/`, `wild/index.json`, `wild_test.glb`, `wildlife_log.txt` |
| Posed NPCs with socket attachments (one GLB per level) | `Extractor npcs out [level...]` | `npc/`, `npc/index.json`, `npcs_log.txt` |
| Compression (`build.py`) | `gltfpack -cc -mi -vpf -noq -km -kv` (skeletons, wildlife and NPCs use `-kn` instead of `-mi`, one node per placement so the map can cull each; enemies are simplified to about 3000 triangles) | site `data/glb/`, `data/skel/`, `data/wild/`, `data/npc/`, `data/enemies/` |
| Web data | `python build_data.py <site>` | site `data/levels.json`, `markers.json`, `catalog.json`, `data/icons/` |

If you host the map and edit a site file by hand, run `python stamp_version.py <site-dir>` so CDNs and browsers pick up the new files (it puts a content hash in the script and stylesheet URLs, which app.js passes on to its data requests).

Other posed props: the skeletons step also exports every other actor placed the same way, meaning anything descended from `Boner_TrapDecoy_C`, `FS_StationarySkeletalMeshActor` or `SkeletalMeshActor` (cutscene actors excluded), plus `Gargoyle_Switch_C`, whose posed mesh is a child component. That covers gargoyles (`GarglerDecoy_00_C`, `Gargoyle_Posed_C`), dead knights, armor and creature statues, decoy robots and the Petrified Garden statues. A prop with no `AnimToPlay` gets an idle for its skeleton (`Enemies.PickIdle`), not the bind pose. Only meshes under `Enemies/Boner_00/` count as skeletons (`placed` in `skel/index.json`, used for markers); the rest are listed under `props`.

Flesh walls: the geometry step writes every `Necro_Deco_Base_C` actor of `300_LIB` and `320_LIB_basement` (the flesh walls, their lumps and the boss door, which dissolve once the necro heads are destroyed) to `glb/300_LIB_flesh.glb` instead of the level's own file, and `build_data.py` adds it as its own area row under Tower of Knowledge. Run the geometry step with both levels (or all) so the file has both.

Skeletons: `Boner_TrapDecoy_C` props are skeletal meshes holding a still pose (`AnimationData.AnimToPlay` on the component). The extractor decodes frame 0 of that pose (ACL-compressed; `LoadVirtualPaths()` in `Program.cs` is what lets CUE4Parse find the `/ACLPlugin/` codec settings) and skins the mesh on the CPU, so the map gets static posed geometry. `HideHead` actors drop the head's triangles. The same class also places dead knights and creature statues; only meshes under `Enemies/Boner_00/` are exported, and `build_data.py` uses `skel/index.json` so the Skeletons markers match the models. `skel_test.glb` has one copy of every distinct pose for checking by eye.

Wildlife: critter actors (lizards, crabs, bugs, rats, birds) are posed with frame 0 of their family's idle animation (`WildIdle` in `Skeletons.cs`), using each actor's full world transform, so lizards and bugs on walls keep their orientation. `wild_test.glb` has one copy per mesh and colour.

The map draws skeletons, wildlife and enemy models only within 150 m of the camera and only when a WebGL2 occlusion query on their bounding box says they are not hidden behind level geometry (`addCullItem` in app.js).

NPCs: `Extractor npcs` poses each NPC's skeletal mesh with the actor's `IdleAnim` (frame 0) and places the static and skeletal meshes attached to its sockets (helmets, sheaths, tools) on the posed bones. All nodes of one NPC are named `<actor>|...`; the map groups nodes by that prefix and culls them as one model.

Enemies: `Extractor enemies` poses each enemy model with frame 0 of an idle animation made for its skeleton (the one whose name best matches the enemy; death, movement and prop variants only when the enemy is named that way). `out/enemies_test.glb` has every model in a row and `out/enemies_pose_log.txt` lists the choices; the four Hell Slimes have no idle and stay in their rest pose.

Other commands: `list` (all pak paths to `files.txt`), `skelprobe out <mesh> <anim>` (bone/skin and pose-decoding diagnostics), `json out <package>...` (dump a package as JSON for inspection), `ismcheck out <level>` (most common meshes in a level and how their materials resolve), `vcprobe out <level> <mesh> [material]` (vertex colors and UV channels per section of a placed mesh; with a material name, writes that section's triangles to `vcprobe_tris.txt`).

## How things are classified

`build_data.py` sorts actors by their blueprint ancestry:

- `CoreItemPickupActor_C` descendants are pickups. `ItemsToGrant` names the item asset, whose `Category` (and folder) picks the marker type. `PickupDifficulty` becomes the Hidden / Out of view / Accessible later label. Key items placed 10+ times become collectibles.
- `EnemySpawner_C` descendants are enemies via `EnemyData` (boss flag, HP, damage, XP, resistances, drops). Spawners targeted by a trigger, crate or group are marked as ambushes.
- `Chest_C`, `Permabreakable_base_C`, `Pot_Base_C`: contents come from `ActorsToActivateOnOpen` / `ActorsToActivateOnBreak`. Breakables with nothing inside are dropped.
- `NPC_Base_C`, `SecretWall_C`, `SecretZoneDoor_BP_C`, `ForbiddenDoor_BaseClass_C`, `RuneButton_C`, `Door_Base_C` (only with `KeyItems` or mechanism-opened), `AQR_KeyRecepticle_BP_C`, `Checkpoint_C`, `Teleporter_C`, `InspectText_C`, levers and lifts, challenge volumes, and wildlife.

Overlapping areas: the game never loads some areas together (Record of Sin and the Final zone share space), but the map does. `MeshClip.KeepBoxes` in `Geometry.cs` clips listed backdrop meshes to a box so they stop cutting through other areas; the export prints `clipped <mesh>: before -> after triangles`. To find which areas a level's meshes cut into, sample its surfaces and look for other levels' actors within a few metres.

Textures: each material gets one color texture, picked from its texture parameters (`MaterialColors` in `Geometry.cs`). Parameters named main/base/albedo/diffuse/color win over alternate/blend/detail/damage/dirt layers; a child material that overrides a single texture (a painting's picture) uses that; cut-out (masked) materials prefer a texture with transparent pixels (except a vertex-painted trim layer, as on the Tower of Knowledge's outer wall: its see-through pixels are filled with the main texture and the surface is drawn opaque), or combine the color texture with a separate mask parameter (cobwebs); engine placeholders, post-process and FX textures are never used. The geometry step writes every decision to `out/materials_log.tsv` (material, blend mode, chosen texture, its transparent fraction, all candidates with scores), which is the place to start when a surface looks wrong.

Geometry: every visible static mesh component (including instanced and spline meshes) at LOD0. Material color is the average of the base color texture times any tint parameter. Skies, FX actors and anything wider than 3 km are skipped. The sea is a see-through surface with `water_shadowdoer` 13 m below it: an opaque, unlit plane in the fog color (`bottofocean`) that hides the depths, as in the game. Unlit materials are exported with `u: 1` and drawn without lighting.

## License

MIT, see `LICENSE`. The three.js and meshoptimizer files in `web/vendor/` are MIT licensed by their own authors. Game content extracted by the build belongs to the game's developers and is not covered by this license.
