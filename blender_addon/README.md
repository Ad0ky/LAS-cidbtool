# LAS CIDB Blender add-on

Add-on version 0.7.0, shipped with CidbTool 0.1.0.

`las_cidb_blender.py` lets you edit Link's Awakening (Switch) room collision in
Blender with readable collision presets, then bake a new CIDB with CidbTool.

- **Import LAS CIDB Collision** reads a `.cidb` (Switch Toolbox export or
  `CidbTool extract`): triangle meshes (u8/u16 indices) and primitive boxes and
  spheres.
- Every face gets a readable material, for example `LAS Stone · Dungeon`,
  `LAS Water · Field` or `LAS Deep water (Swamp) · Dungeon`. The material holds
  the full contact code, so you change the collision type by changing the
  material.
- The **Collision Presets** panel lists all 150 contact codes used by the
  retail game, with category, effects, confidence and an example room.
- **Bake** runs CidbTool for you and writes the new CIDB.

The add-on does not write CIDB itself. CidbTool's PhysX cooker is verified
byte-exact against every retail mesh, including the per-triangle edge flags,
so new geometry gets the same data the game's own tools would produce.

## Install

1. Get `CidbTool.exe`: it is in the root of the release zip, or build it from
   source (see the main [README](../README.md)).
2. In Blender (3.6 or newer): **Edit → Preferences → Add-ons → Install…** and
   pick `blender_addon/las_cidb_blender.py`. Enable **Import-Export: LAS CIDB
   Collision**.
3. In the add-on's preferences set **CidbTool.exe** to the path from step 1.
   Optional: **Presets JSON** can point to `presets/las_collision_presets.json`
   to use a regenerated library instead of the embedded copy.

The tools are in the 3D View sidebar (N) under **LAS Collision**.

## Workflow

1. In Switch Toolbox export the room model (DAE) and its embedded collision
   (CIDB). Import the DAE into Blender.
2. **Import CIDB**. Collision appears in the `LAS_Collision` collection,
   aligned with the DAE. Don't rotate it.
3. Edit freely: move vertices, add or delete faces, add new objects in
   `LAS_Collision`, move/scale/rotate primitives.
4. Assign collision types:
   - **Collision Presets**: filter by category (Ground, Wall, Water, Hazard,
     Overlay, Invisible wall, Special), search by name or hex code, tick the
     star to see only the most-used code per material/area. Pick one and press
     **Assign Preset**. In Edit Mode it applies to the selected faces; in
     Object Mode to all faces of the selected objects.
   - **Advanced: Compose Code** builds a code from Surface (footstep material),
     Behaviour (surface class) and Area tag. It warns when the result is not
     used anywhere in the retail game.
   - Ordinary Blender material assignment works too: any `LAS …` material can
     be reused on other faces.
5. **Bake** (panel at the bottom): choose the mode, check the template and
   output paths, press **Bake CIDB**. The last lines of CidbTool's output are
   shown in the panel (full output in Blender's system console).
6. Import the baked CIDB into the BFRES in Switch Toolbox, save, and place it in
   the RomFS overlay. The baked file has the same form as the template: a
   template exported by Switch Toolbox gives a file Switch Toolbox can import
   directly; a raw `CidbTool extract` template needs `CidbTool wrap-toolbox`
   first.

**Export OBJ** writes the same bake OBJ without running CidbTool, plus a
`.las_collision.json` manifest naming the CidbTool command.

### Bake modes

| Mode | CidbTool command | Allowed edits |
|---|---|---|
| Any topology (default) | `cook-blender-physx` | Anything: new/deleted faces, new objects, new contact codes, primitives. |
| Any topology, keep offsets | `cook-blender-physx-layout` | Like above, but every section keeps its template offset; fails if a mesh no longer fits, and primitives must stay unchanged. |
| Same topology only | `rebuild-geometry` | Move vertices and switch faces to codes already in that mesh's table. |

An untouched room bakes back byte-identically in all three modes (tested on
TownWell, KeyCavern and a sample of 30 field, dungeon and actor CIDBs).

## What the presets mean

A contact code has four parts. Only the first two change gameplay.

| Bits | Part | Effect |
|---|---|---|
| 0-5 | Surface (material) | Footstep and landing sounds. Water (21) and Swamp (22) also slow walking to 0.75×. |
| 6-9 | Behaviour (surface class) | 5 = pit (fall and respawn), 6 = pass-through overlay, 7 = deep water (dive/swim), 8 = lava. 1, 3, 4, 9, 10 are used by retail but their effect is not confirmed yet. |
| 10-12 | Area tag | Field / House / Dungeon convention. Not read by the game. |
| 14, 18, 19 | Tags | Authoring tags (bit 19 is common on ledges). Not read by the game. |

Confidence labels: `confirmed-ingame` (seen in a gameplay test),
`confirmed-code` (found in the game's code), `inferred`, `unknown`.

**Auto-jump ledges are not a floor type.** Jumping off an edge comes from
`AirWallAutojump` / `AirWallAutojumpD` actors placed in the level, which the
level editor handles. Slowing down is the Water/Swamp surface.

## Primitive shapes

Boxes and spheres become objects named `collision_box_###` /
`collision_sphere_###`. Their object scale is the half-size (box) or radius
(sphere); move, rotate and scale them like any object. **Add Box / Add
Sphere** (Primitive Shapes panel) creates a new one at the 3D cursor with the
selected preset. Deleting a primitive object removes it from the baked CIDB.
The `Group` value is copied from retail; its meaning is not known yet, so keep
it unless you are experimenting.

## Notes

- Face data stays lossless: each imported face keeps its original slot, face
  remap and edge byte as face attributes (`las_surface_id`, `las_face_remap`,
  `las_extra_flag`). Unchanged faces are written with that identity, changed or
  new faces with their full code.
- CidbTool splits an object with more than 512 vertices or triangles into
  several collision sections (the largest retail mesh has 489 triangles).
- Materials from add-on 0.6 and older (`LAS_CIDB_S…`, `LAS_Contact_0x…`,
  `LAS_Surface_…`) are still accepted on export.
