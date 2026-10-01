# CidbTool

Version 0.1.0

CidbTool reads, edits and rebuilds the **CIDB** collision data embedded in
*The Legend of Zelda: Link's Awakening* (Nintendo Switch, 2019) room and actor
`.bfres` files. A companion Blender add-on lets you edit collision visually
with readable collision types ("Stone", "Water", "Lava", "Ladder", …) and bake
it with one button.

- **Exact cooker.** CidbTool rebuilds CIDB collision with a port of the PhysX
  3.4 triangle-mesh cooker that the game's own files were made with. Every
  retail mesh re-cooks byte-identically: triangle order, R-tree, mesh bounds
  and per-triangle edge flags.
- **Any topology.** Add, delete and reshape faces, add new collision objects,
  and add, move or delete box and sphere shapes.
- **Readable collision types.** All 150 contact codes used by the game are
  named and categorised in a preset library, with their known gameplay effects.
- **Never touches the source.** The input BFRES and RomFS files are only read.

The binary format is documented in [CIDB_FORMAT.md](CIDB_FORMAT.md).
Open gameplay questions and their in-game test plan are in
[INGAME_TESTS.md](INGAME_TESTS.md).

No game files are included. You need your own dump of the game's RomFS.

## Download

Windows users can take the prebuilt `CidbTool-<version>-win-x64.zip` from the
GitHub Releases page. It contains `CidbTool.exe` (self-contained, no .NET
install needed), the Blender add-on, the preset library and these documents.
Unzip it anywhere and run `CidbTool.exe` from a terminal.

To build from source instead, see below.

## Requirements

- [.NET 6 SDK](https://dotnet.microsoft.com/download/dotnet/6.0) or newer
  (the project targets `net6.0`).
- [Switch Toolbox](https://github.com/KillzXGaming/Switch-Toolbox) to export
  room models (DAE) and to put a baked CIDB back into a BFRES.
- Blender 3.6 or newer for the add-on (optional).

## Build

From the repository root:

```sh
dotnet build CidbTool.csproj -c Release
```

The program is written to `bin/Release/net6.0/` (`CidbTool.exe` on Windows,
`CidbTool` elsewhere). Run `CidbTool --help` for the command list and
`CidbTool --version` for the version.

The examples below write `CidbTool` for the program and `<romfs>` for the
root of your RomFS dump (the folder that contains `region_common`).

## Quick start: edit a room in Blender

1. In Switch Toolbox, open a room such as `<romfs>/region_common/map/TownWell_01A.bfres`,
   export the model as DAE and the embedded CIDB external file as
   `TownWell_01A.cidb`. (Or run `CidbTool extract`, see below.)
2. Install the add-on and set the CidbTool path in its preferences
   ([blender_addon/README.md](blender_addon/README.md)).
3. In Blender, import the DAE, then **Import LAS CIDB Collision**.
4. Edit geometry and assign collision presets in the **LAS Collision** sidebar.
5. Press **Bake CIDB**, then import the baked file back into the BFRES with
   Switch Toolbox and place the BFRES in your mod's RomFS.

## Commands

### Look and extract

| Command | What it does |
|---|---|
| `inspect <room.bfres>` | Report the embedded CIDB containers. |
| `extract <room.bfres> [out-dir]` | Write each CIDB as a raw `.cidb` slice plus a JSON manifest. |
| `batch <map-dir> [out-dir]` | `extract` for every BFRES in a folder. |
| `decode <room.bfres\|file.cidb> [out-dir]` | Write the collision as OBJ (raw and Blender-aligned), a JSON report and top/side SVG views. |
| `wrap-toolbox <in.cidb> <out.cidb>` | Add the 4-byte `04 00 00 00` header Switch Toolbox expects when importing an external file. |

```sh
CidbTool extract "<romfs>/region_common/map/TownWell_01A.bfres" out
CidbTool decode out/TownWell_01A.cidb out/decoded
```

Without an output folder, `extract`, `batch` and `decode` write next to the
input (`_cidb`, `_cidb_geometry`).

**Switch Toolbox wrapper.** CIDB files exported by Switch Toolbox start with a
4-byte wrapper; CidbTool accepts both forms and every rebuild keeps the form of
its template. A baked file made from a raw `extract` slice needs `wrap-toolbox`
before Switch Toolbox can import it.

### Bake collision

All bake commands take a **template** CIDB from the target room, an OBJ, and an
output path. They never modify the template.

| Command | Allowed edits |
|---|---|
| `cook-blender-physx <template.cidb> <in.obj> <out.cidb>` | **Recommended.** Any topology: new/deleted faces and objects, new contact codes, primitive shapes. Cooked with the verified PhysX cooker. |
| `cook-blender-physx-layout <template.cidb> <in.obj> <out.cidb>` | Like above, but every section keeps its template offset. Fails if a mesh no longer fits, and primitives must be unchanged. |
| `rebuild-geometry <template.cidb> <in.obj> <out.cidb>` | Same topology: move vertices and change face slots; refits the existing R-tree. |
| `rebuild-surface <template.cidb> <in.obj> <out.cidb>` | Only per-face slot changes; geometry must be identical. |
| `cook-blender-template-tree <template.cidb> <in.obj> <out.cidb>` | Experimental: new connectivity with the template's exact vertex and triangle counts. |

The OBJ is normally written by the Blender add-on (**Export OBJ** or **Bake**).
A `decode` Blender OBJ (`*.collision.blender.obj`) also works and bakes back
byte-identically. OBJ conventions understood by the bakers:

- Coordinates in Blender/DAE space: game `(X, Y, Z)` = Blender `(X, Z, -Y)`.
- One `o` object per collision section. Objects above 512 vertices or 512
  triangles are split (the largest retail mesh has 489 triangles).
- Face materials:
  - `cidb_contact_<hex>` gives the face that full contact code, for example
    `cidb_contact_0000020F` for lava. The code is added to the section's table
    if needed.
  - `cidb_surface_<slot>[_extra_<n>][_remap_<n>]` keeps an existing slot of the
    template mesh (and its original face order).
- `#las_primitives` / `#las_primitive …` lines describe box and sphere shapes
  (see [CIDB_FORMAT.md](CIDB_FORMAT.md#primitive-shape-sections)).

Collision faces are one-sided: floors must face up, walls must face into the
playable space.

`cook-blender-physx` accepts an optional SAH preset (0-14) as a fourth
argument; the default 8 is what the game used.

### Catalogs and presets

| Command | What it does |
|---|---|
| `export-presets <out.json> <dir> [dir…]` | Build the collision preset library from map/actor folders. |
| `contact-catalog <map-dir> [out.json]` | List every contact-code table and its mesh-local slots. |
| `surface-catalog <map-dir> [out.json]` | List the raw per-triangle slot values and where they are used. |
| `audit <map-dir\|room.bfres> [out.json]` | Compare headers, descriptors, R-tree headers and triangle metadata. |
| `export-surface-layers <room.bfres\|file.cidb> [out-dir]` | One preview OBJ per slot value (visual reference only, not bake input). |

```sh
CidbTool export-presets presets/las_collision_presets.json \
  "<romfs>/region_common/map" "<romfs>/region_common/actor"
```

### Verification

These re-run the proofs that the cooker matches the game's data:

| Command | What it checks |
|---|---|
| `verify-cooker <dir\|room.bfres> [report.json] [--only sah8] [--limit N]` | Re-cooks every mesh from its face remap; compares triangle order, R-tree pages and header. |
| `verify-recook <dir\|room.bfres> [--layout]` | Rebuilds whole CIDB containers through the bake path; compares the bytes. |
| `verify-edges <dir\|room.bfres> [--show N]` | Recomputes the per-triangle edge flags; compares with the game's. |

Results on the retail game (map and actor folders):

| Check | Result |
|---|---|
| `verify-cooker --only sah8` | 4,657 / 4,657 meshes identical |
| `verify-recook` and `--layout` | 1,174 / 1,174 CIDB containers identical |
| `verify-edges` | 317,126 / 317,126 triangles identical |

Diagnostics: set `CIDB_VERIFY_DUMP=<mesh index>` to print retail vs cooked
data for one mesh, or `CIDB_TRACE_SPLIT=1` to print every R-tree split.

### Research and test helpers

| Command | What it does |
|---|---|
| `translate <in.cidb> <x> <y> <z> <out.cidb>` | Offset all collision (same topology) for alignment tests. |
| `create-floor-surface-tests <in.cidb> <out-dir>` | Variants that repaint the largest flat floor with slots 1-10. |
| `create-topology-smoke-test <in.cidb> <out.cidb>` | Split one floor triangle into three. |
| `create-barrier-smoke-test <in.cidb> <out.cidb>` | Add an invisible wall (TownWell test). |
| `restore-mesh <orig.cidb> <cooked.cidb> <mesh#> <out.cidb>` | Copy one original mesh back into a cooked CIDB. |
| `rebuild-rtree-layout <in.cidb> <out.cidb>` | Rebuild every R-tree with the early prototype cooker, inside the original section layout. |
| `cook-blender`, `cook-blender-layout`, `cook-blender-leaf4`, `cook-blender-spatial`, `cook-blender-physx-quick` | Earlier cooker prototypes, kept for comparison. Use `cook-blender-physx` instead. |

## Collision preset library

[presets/las_collision_presets.json](presets/las_collision_presets.json)
lists all 150 contact codes the game uses, each with:

- a readable name, e.g. `Stone · Dungeon`, `Deep water (Swamp) · Field`;
- a category: Ground, Wall, Water, Hazard, Overlay, Ladder, Invisible wall or
  Special;
- its known effects;
- a confidence level: `confirmed-ingame`, `confirmed-code`, `inferred` or
  `unknown`;
- usage counts and an example room.

`recommended: true` marks the most used code for each category, material and
area. The file also holds the field tables (48 footstep materials, 16 surface
classes, area tags and flags), so tools can compose new codes. The Blender
add-on embeds a copy.

A contact code in short (details in [CIDB_FORMAT.md](CIDB_FORMAT.md)):

| Bits | Meaning |
|---|---|
| 0-5 | Footstep material. Water (21) and Swamp (22) also slow walking to 0.75×. |
| 6-9 | Surface class: 5 pit, 6 pass-through, 7 deep water, 8 lava (confirmed in code); 3 ladder and 4 ladder top (inferred); 1, 9, 10 not yet known. |
| 10-12 | Area tag (field / house / dungeon). Not read by the game. |
| 14, 18, 19 | Authoring tags. Not read by the game. |

Auto-jump ledges are level actors (`AirWallAutojump*`), not a collision type.

## Repository layout

| Path | Contents |
|---|---|
| `Program.cs`, `CidbTool.csproj` | The command-line tool. |
| `blender_addon/` | Blender add-on and its README. |
| `presets/` | Collision preset library (generated by `export-presets`). |
| `CIDB_FORMAT.md` | Format documentation and reverse-engineering notes. |
| `INGAME_TESTS.md` | Test plan for the collision classes not yet confirmed. |
| `CHANGELOG.md` | Release notes. |
| `LICENSE` | MIT License. |
| `THIRD_PARTY_NOTICES.md` | License of the PhysX 3.4 code that the cooker is ported from. |

## Legal

CidbTool is released under the MIT License (see [LICENSE](LICENSE)).

This is an unofficial fan tool, not affiliated with or endorsed by Nintendo or
Grezzo. It contains no game data. The R-tree cooker and edge-flag code are
ports of NVIDIA PhysX 3.4, used under its BSD 3-clause license (see
[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)).
