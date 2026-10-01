# Changelog

## 0.1.0

First public release.

CidbTool
- Read, extract and decode CIDB collision from room and actor BFRES files
  (meshes with u8/u16 indices, box and sphere primitives).
- `cook-blender-physx`: bake any collision topology with a port of the PhysX
  3.4 cooker, verified byte-identical on every retail mesh (4,657) and
  container (1,174); `-layout` variant keeps section offsets.
- Per-triangle edge flags derived like PhysX (`verify-edges`: 317,126/317,126
  retail triangles).
- Full contact codes per face (`cidb_contact_<hex>`), added to the section's
  contact table on bake.
- Authored primitive shapes (`#las_primitive` lines): add, move, retype,
  delete boxes and spheres.
- Same-topology edits (`rebuild-geometry`, `rebuild-surface`) that keep the
  retail R-tree layout.
- `export-presets`, `contact-catalog`, `surface-catalog`, `audit` and the
  `verify-cooker` / `verify-recook` / `verify-edges` checks.

Blender add-on 0.7.0
- Import CIDB meshes and primitives; one readable material per contact code.
- Preset browser (categories, search, recommended), code composer, box and
  sphere tools, Bake button that runs CidbTool.
- Untouched rooms bake back byte-identically.

Data
- `presets/las_collision_presets.json`: all 150 retail contact codes, named
  and categorised.
