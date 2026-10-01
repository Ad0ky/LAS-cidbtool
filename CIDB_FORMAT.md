# CIDB

`CIDB` is the collision-data container embedded in many Link's Awakening
room-model BFRES files. It stores one or more triangle meshes, each with raw
per-triangle surface IDs and a serialized PhysX R-tree used for broad-phase
collision queries.

This page documents the current state of reverse engineering, not an official
specification. Fields are labelled **verified** only where they have been
decoded from the retail RomFS and exercised by CidbTool's round-trip checks.
Do not overwrite fields labelled **unknown**.

## Scope and evidence

The format description is based on the retail game with the `v131072`
update, mainly:

```text
romfs/region_common/map/*.bfres     room models (1,052 embedded CIDBs)
romfs/region_common/actor/*.bfres   actor models (122 embedded CIDBs)
```

The reader decodes every embedded CIDB in both directories. The `RTRE` structure's field order, page geometry, and mesh-tail
layout match the public PhysX 3.4 R-tree triangle-mesh serializer. The shipped
executable is stripped, so this is format evidence rather than a retained
PhysX symbol-name claim.

CIDB is not the game's only collision-related data. A room's LEB grid, actor
collision, `AreaWaterBox`, `WaterEvent`, and other systems can affect gameplay
independently of this file.

## Location in BFRES

CIDB is embedded rather than stored as a separate regular ROMFS file.

1. Open a room model in `region_common/map`.
2. Search for the ASCII magic `CIDB` (`43 49 44 42`).
3. The lossless container ends at the BFRES relocation-table offset stored in
   the BFRES header at `0x18`.

The `CIDB` magic must occur before that relocation table. A Switch Toolbox
export of an embedded external file can add a small wrapper before the magic;
the payload itself still starts at `CIDB`.

## Conventions

Unless a table says otherwise, integer and float fields are little-endian.

| Type | Meaning |
| --- | --- |
| `u8`, `u16`, `u32` | unsigned integer, little-endian |
| `be_u32` | unsigned 32-bit integer, big-endian |
| `f32` | IEEE-754 single-precision float, little-endian |
| `be_f32` | IEEE-754 single-precision float, big-endian |

Raw CIDB vertices use `(X, Y, Z)`. `X` and `Z` form the floor plane; `Y` is
the vertical/gravity axis. 

## CIDB container

All decoded map containers start with this little-endian header.

| Offset | Type | Description |
| ---: | --- | --- |
| `0x00` | `char[4]` | Magic: `CIDB` |
| `0x04` | `u32` | Header size. Observed value: `0x10`. |
| `0x08` | `u32` | Unknown. Observed as `0` in the sampled map files. |
| `0x0C` | `u32` | Section count. Varies from 1 to 43 across the retail archive. |
| `0x10` | `u32[section_count]` | Offsets of CIDB sections, relative to the CIDB start. |

Every section is a 0x2C-byte descriptor followed by a payload and a
contact-code table (see the descriptor table below). There are two kinds:

* **Triangle meshes** (`NXS\x01MESH` payload), descriptor `+0x10` low byte `1` or `2`.
* **Primitive shapes** (box or sphere), descriptor `+0x10` low byte `0`.
  2,853 primitive sections exist in 398 retail map BFRES files (3,070 including actors).

Ordering and placement (**verified**: every retail container rebuilds
byte-identically with these rules):

* All mesh sections come first, then all primitive sections. The first
  section is always mesh kind `2`.
* The first section starts at the next 0x80 boundary after the header.
* Each later section starts at the next 0x80 boundary **strictly after** the
  previous section's end rounded up to 4 bytes. An end that is already on a
  0x80 boundary therefore still gets a full 0x80 of zero padding.
* The container ends right after the last section; there is no trailing
  alignment. A BFRES slice may continue with zero padding or straight into the
  next embedded file (`04 00 00 00 'CIDB'` …), which is not part of this CIDB.

## NXS/MESH collision record

Every decoded collision mesh starts with the eight-byte magic
`4E 58 53 01 4D 45 53 48` (`NXS\x01MESH`). The mesh's variable data immediately
follows this 28-byte header.

| Offset | Type | Description |
| ---: | --- | --- |
| `0x00` | `char[8]` | Magic: `NXS\x01MESH` |
| `0x08` | `u32` | PhysX mesh serialization version. Observed value: `0x0E`. |
| `0x0C` | `u32` | PhysX midphase ID. Observed value: `0`, consistent with the following `RTRE` R-tree midphase. |
| `0x10` | `u32` | PhysX serialization flags. `0x07` = material-index array + face-remap array + `u8` indices; `0x0B` = material-index array + face-remap array + `u16` indices. |
| `0x14` | `u32` | Vertex count (`V`). |
| `0x18` | `u32` | Triangle count (`T`). |
| `0x1C` | `f32[3 × V]` | Vertex positions in raw `(X, Y, Z)` order. |

### Outer descriptor and contact-code table

In the retail CIDBs examined so far, an `NXS\x01MESH` record is immediately
preceded by a 0x2C-byte outer descriptor. This is not merely alignment data:

| Descriptor-relative offset | Type | Established meaning |
| ---: | --- | --- |
| `+0x04` | `u32` | Payload length (the NXS/MESH record, or the primitive's shape floats). |
| `+0x08` | `u32` | Byte length of the following material/contact-code table; divisible by four. |
| `+0x0C` | `u32` | Low `u16`: primitive shape type (`1` = box, `3` = sphere; `0` for meshes). High `u16`: a small per-section group number (meaning not yet named). |
| `+0x10` | `u32` | Section kind: `0xFFFFFF00` = primitive, `0xFFFFFF01` / `0xFFFFFF02` = triangle mesh. The difference between mesh kinds 1 and 2 is not the index width; the first section is always kind 2. |
| `+0x14` | `f32[3]` | Primitives: position. |
| `+0x20` | `f32[3]` | Primitives: rotation in radians. 2,894 of the 3,070 primitive sections in the map and actor archives are `0, 0, 0`; most others rotate about Y by π/2 or π. |

#### Primitive shape sections

| Shape (`+0x0C` low `u16`) | Payload at `+0x2C` | Example |
| --- | --- | --- |
| `1` box | `f32[3]` half-extents (payload length `0x0C`) | `PanelLv03KeyCavern_08B`: 0.75 × 0.75 × 0.75 blocks, contact `0xC` |
| `3` sphere | `f32` radius (payload length `0x04`) | `Coconut`: radius 0.5, contact `0x84C49` |

Descriptor `+0x00` is `0` on every retail section. The high `u16` at `+0x0C`
(group) takes the values 0, 1, 5, 6, 10 and 17 on primitives (meshes: 0-20);
its meaning is still unknown.

Each primitive carries its own contact-code table (normally one `u32`; 24
retail primitives have two or three), with the same meaning as a mesh slot.

**Authoring primitives.** A bake OBJ may contain primitive lines in game
coordinates:

```text
#las_primitives
#las_primitive box group=0 pos=-2.25,0.75,-0.75 rot=0,0,0 size=0.75,0.75,0.75 codes=0xC
#las_primitive sphere group=1 pos=0,-0.05,0 rot=0,0,0 size=0.5 codes=0x84C49
```

`size` is the box half-extents or the sphere radius; `rot` is in radians.
When `#las_primitives` is present the listed shapes replace the template's
(an empty list deletes them all); without it the template's primitives are
copied unchanged, after the cooked meshes and in their original order. A
room's primitives written this way rebuild byte-identically.

The contact-code table begins at `nxs_offset + descriptor[+0x04]` and consists
of `descriptor[+0x08] / 4` little-endian `u32` values. The table is followed by
padding until the next CIDB descriptor. It is part of the CIDB, not BFRES
render-material metadata or a value invented by the executable at load time.

For example, TownWell mesh 1 serializes the four words
`0x00000029, 0x00000017, 0x0000004B, 0x0008000B` directly after its NXS
payload. Its material-table-index `1` resolves to `0x17`, the observed shallow
water contact code. Tools must preserve every full `u32` unchanged: bits 0-9
drive gameplay and the higher bits are authoring tags (see the bit layout
below).

### Triangle index width

PhysX (`TriangleMeshBuilder::save`) chooses the width from the **largest
vertex index referenced by a triangle**, not from the vertex count:

| Largest referenced vertex index | Triangle index type | Bytes per triangle |
| --- | --- | ---: |
| `<= 0xFF` | `u8[3]` (flags `0x07`) | 3 |
| `<= 0xFFFF` | `u16[3]` (flags `0x0B`) | 6 |

These usually coincide with `V <= 255`, but a 256-vertex mesh still uses `u8`.

Triangle indices follow the vertex array and are ordered `[a, b, c]`. The
reader verifies that all indices are below `V`. Collision is one-sided; a
custom floor must have its normal facing raw `+Y`, and a wall's normal must
face the playable volume.

### Per-triangle material-table indices

The triangle-index array is followed by exactly `T` little-endian `u16`
values. Entry `i` belongs to triangle `i`.

```text
surface_ids_offset = 0x1C + (V * 12) + (T * 3 * index_width)
surface_id[i]      = u16(surface_ids_offset + i * 2)
```

These values are PhysX `PxMaterialTableIndex` entries: per-triangle indices
into the CIDB mesh's immediately following `u32` contact-code table. They are
not BFRES render-material names and are not, by themselves, a global gameplay
material enum. A complete scan of the retail map archive found values `0`
through `10`.

This is why the same raw value can legitimately result in different footsteps
in two rooms: the mesh stores an index, while each CIDB mesh serializes its own
contact-code table. The runtime resolves a hit's triangle material index through
that per-mesh table.

The field order and flags match the public
[PhysX 3.4 triangle-mesh serializer](https://github.com/NVIDIAGameWorks/PhysX-3.4/blob/master/PhysX_3.4/Source/PhysXCooking/src/mesh/TriangleMeshBuilder.cpp),
which writes `mMaterialIndices` as a `u16` array, and its
[shape material resolver](https://github.com/NVIDIAGameWorks/PhysX-3.4/blob/master/PhysX_3.4/Source/PhysX/src/NpShape.cpp),
which obtains the hit triangle's material index then resolves it through the
shape material array.

#### Runtime contact-material evidence

Emulator debugger snapshots of a stationary Link show the resolved contact code
in the player state at `PlayerLink + 0x1700`. The low six bits are the
footstep material (see the bit layout below); the higher bits vary between room
families, so tools must preserve the complete value.

| Low 6-bit code | Full value(s) observed | Vanilla reference |
| ---: | --- | --- |
| `1` | `0x0C01` | Ordinary grass: `Field_10C`, `Field_11A` |
| `5` | `0x1805`, `0x0C05` | Sand: `BananaHouse_01A`, `Field_16E` |
| `7` | `0x0C07` | Yellow-earth paths: `Field_10A`, `Field_14A` |
| `10` | `0x180A` | Wood: `BananaHouse_01A` |
| `12` | `0x0C0C` | Stone/tile path: `Field_10C` |
| `21` | `0x1815` | BananaHouse shallow water |
| `23` | `0x0017` | TownWell shallow water |
| `38` | `0x0C26` | Field ground/path samples: `Field_10C`, `Field_15D` |
| `41` | `0x0029` | TownWell dry cave floor (previously labelled cement) |

There are several water-like contact codes (`21`, `23`, `47`), so a baker must
not collapse every water triangle to one guessed material.

The nearby `PlayerCollision` status result remains `4` on sand and wood, so it
is not the material. The player field is therefore the game-side
contact-material code used by footstep logic.

Static EXEFS control-flow analysis identifies the immediate writer. The player
update calls `FUN_7100825A20`; it returns a `u32` through a stack output, which
is written to `PlayerLink + 0x1700` at `0x7100D54514`. Inside that resolver,
after an indirect collision/contact query, it obtains a `u16` table index and
loads the resulting full `u32` from `table[index]` before returning it. This is
the runtime confirmation of the **contact-index-to-contact-code table**. The
same table is serialized directly after the NXS payload in the CIDB outer
record, as described above.

The direct live capture, in `TownWell_01A` while standing in water, closes the
CIDB-to-gameplay link. The resolver's called method at live `0x81346410`
(static `0x7101342410`) executes:

```asm
ldr  x8, [x0, #0x60]
ldrh w0, [x8, w1, uxtw #1]
ret
```

`w1` was contact triangle `83`; the `u16` read from the live mesh's
`x0 + 0x60` array was `1`, and the caller returned `x0 = 1`. The original
TownWell mesh 1 CIDB serializes `surface_id[83] = 1` at that same triangle.
The resolver then loads `table[1] = 0x00000017`, exactly matching
`PlayerLink + 0x1700` in that water position. Thus the `u16[T]` CIDB array is
the runtime per-triangle material-slot lookup, not merely a correlated field.

The captured shape table's first four `u32` values were `0x29`, `0x17`,
`0x4B`, and `0x0008000B`; TownWell's CIDB uses raw slots `0` through `3`.
This confirms the slot-1 mapping directly and establishes the remaining
entries as the active per-shape mapping range.

#### Verified vanilla CIDB profile examples

The following values can now be obtained directly from the original CIDB; no
BFRES render material lookup or gameplay re-test is necessary. The `slot` is
local to the listed collision mesh.

| Room / mesh | Slot | Serialized full code | Observed effect |
| --- | ---: | ---: | --- |
| `Field_10C`, mesh 1 | 4 | `0x0C01` | Grass |
| `Field_10C`, mesh 1 | 1 | `0x0C26` | Field earth/ground |
| `Field_10C`, mesh 1 | 2 | `0x0C0C` | Tile/stone path |
| `BananaHouse_01A`, mesh 1 | 0 | `0x180A` | Wood |
| `BananaHouse_01A`, mesh 1 | 2 | `0x1805` | Sand |
| `BananaHouse_01A`, mesh 1 | 3 | `0x1815` | Shallow-water variant |
| `TownWell_01A`, mesh 1 | 0 | `0x0029` | Dry cave floor |
| `TownWell_01A`, mesh 1 | 1 | `0x0017` | Shallow-water variant |

`contact-catalog` reads all these tables from the retail map archive. Its
output lists the full code, low six-bit movement/footstep code, source room,
mesh-local slot, and triangle count. It is the appropriate automated source
for selecting a donor profile; raw slot numbers must never be treated as
global names.

`PlayerLink__Walk` tests whether the low six-bit code is `21` or `22` and
applies a `0.75` movement multiplier only in that case; code `23` (also
WATER) has no slow-down.

### Contact-code bit layout and game-side meaning (static EXEFS analysis)

This section was derived from the retail `main` NSO (text segment addresses
below are the offsets in the decompressed NSO, i.e. Ghidra address minus
`0x7100000000`) plus a scan of every CIDB in the retail map archive. Items
marked *(inferred)* rest on geometry and asset-name evidence, not on an
in-game test.

**Corpus facts.** The retail map archive holds 148 distinct full `u32` contact
codes across 1,049 CIDB-bearing files (1,149 BFRES scanned). They collapse to
39 distinct low-six-bit values (150 codes including actor models). All of
them, named and categorised, are in
[presets/las_collision_presets.json](presets/las_collision_presets.json)
(`CidbTool export-presets`).

**Resolver chain (confirmed).**

1. `0x825A20` is the contact query. It requires a triangle-mesh shape
   (geometry type 5), asks the mesh for the hit triangle's `u16` slot
   (`0xFFFF` = none), then loads `table[slot]` and returns the whole `u32`
   unchanged. No bit is altered here.
2. The player update stores that `u32` at `PlayerLink + 0x1700` (`0xD54514`).
3. An actor that carries a `GroundAttr` component (looked up by name, string at
   `0x16BA17E`) can override the material: at `0xD54B00`/`0xD54C3C` the player
   replaces only bits 0-5 with the `u16` at `component + 0x58`. Moving floors,
   crates and similar objects therefore report their own material instead of
   the ground under them. Bits 6+ keep the CIDB value.
4. At `0xD546E8` bits 0-5 are copied into the footstep component
   (`component + 0x70`, `u16`). Its getter (vtable slot `+0x30`, e.g. `0xE521A0`)
   feeds the sound-name builder at `0xE5044C`.
5. The builder formats `SE_FT_{action}_{material}_R{variant}` (and
   `SE_FTM_...`, format strings at `0x16B6F8E` / `0x16CEE11`). `{material}` is
   the string at index `low6` of a 48-entry table (see below); `{action}` comes
   from a 10-entry action table (None, WALK, RUN, DASH1, DASH2, JUMP, LAND,
   WALK, RUN, LAND).

**Bit layout of the `u32`.**

| Bits | Mask | Role | Evidence |
| ---: | ---: | --- | --- |
| 0-5 | `0x3F` | Material index. Selects footstep/landing sound name; values `21` and `22` also apply a `0.75` walk-speed multiplier (`ldrh ... and 0x3f ... sub 0x15 ... cmp 1` at `0xDA7128`, `0xDB80B8`, `0xDD0578` and others). | Confirmed in code. |
| 6-9 | `0x3C0` | 4-bit surface-class field `F`. Tested by `ubfx w,w,#6,#4` / `and 0x3C0` in about 40 player-state functions. Only `F` = 5, 6, 7, 8 are tested explicitly (`F-5 < 2`, `F-5 < 4`, `F == 6` = `0x180`, `F == 7` = `0x1C0`, `F == 8` = `0x200`). | Comparison sites confirmed; meaning of each value *(inferred)* below. |
| 10+ | `>= 0x400` | Area/behaviour flag bits. **Exhaustively checked: no consumer found.** All 65 static reads of `PlayerLink + 0x1700` in `main` were enumerated and every one either tests bits 0-5 (via the `(F-5) < N` idiom), tests bits 6-9 against `0x3C0`, or copies the whole word unmodified (the `GroundAttr` override, the raw getter at `0xD4B7A4`). A separate search of the whole `.text` segment for any `LSR #10`/`UBFX` isolating bits 10+ found 7 sites, none touching this code path (they belong to unrelated animation/physics bitfields). Observed bits: `0x400`, `0x800`, `0x1000`, `0x4000`, `0x40000`, `0x80000`. | Very likely Grezzo-editor/authoring metadata (area grouping, auto-jump ledge tagging) baked into the CIDB but not read by this game binary at runtime. |

**Material name table (48 entries, index = `low6`).** Built by a static
initialiser at `0xE5099C` into a bss table at `0x1CC1C40` (40-byte entries), so
it does not exist as a literal in the file. Index: name.

| Idx | Name | Idx | Name | Idx | Name | Idx | Name |
| ---: | --- | ---: | --- | ---: | --- | ---: | --- |
| 0 | GRASS | 12 | MARBLE | 24 | SWAMP | 36 | SAND |
| 1 | GRASS | 13 | MARBLE | 25 | MARBLE | 37 | SAND |
| 2 | TURF | 14 | STONE | 26 | CARPET | 38 | DIRT |
| 3 | GRASS | 15 | MUD | 27 | WOOD2CR | 39 | DIRT |
| 4 | GRASS | 16 | MARBLE | 28 | CARPET | 40 | GRASS |
| 5 | SAND | 17 | STONE | 29 | STONE | 41 | DIRT |
| 6 | GRAVEL | 18 | STONE | 30 | STONE | 42 | DIRT |
| 7 | DIRT | 19 | CARPET | 31 | MUD | 43 | DIRT |
| 8 | DIRT | 20 | MARBLE | 32 | DIRT | 44 | SAND |
| 9 | WOOD | 21 | WATER | 33 | CARPET | 45 | DIRT |
| 10 | WOOD3HO | 22 | SWAMP | 34 | STONE | 46 | DIRT |
| 11 | STONE | 23 | WATER | 35 | CARPET | 47 | WATER |

All nine low-six values that were previously verified by gameplay capture
(1, 5, 7, 10, 12, 21, 23, 38, 41) agree with this table. Index 0 is the most
common code in the corpus and is named GRASS here, but 62,647 of its triangles
are 68% walls, so it is effectively a "no special material" default; do not
treat it as evidence of grass.

**Surface-class field `F` (bits 6-9), corpus geometry.** Orientation of every
triangle carrying each value (normal `+Y` = up):

| `F` | Triangles | Orientation | Example full codes | Reading |
| ---: | ---: | --- | --- | --- |
| 0 | 125,383 | 54% up, 28% wall, 16% slope | `0x0C01`, `0x0C0C`, `0x0B` | Ordinary geometry. |
| 1 | 166,186 | 68% wall, 16% slope, 14% up | `0x4B`, `0x4C`, `0x40`, `0x0C48` | Wall/cliff-side collision *(inferred)*. Its floors include the raised ground under field tree lines (`0xC43`, Field_06C/07A). |
| 3 | 740 | 100% wall, all facing ±Z | `0xC0`, `0xCB`, `0xCC`, `0xCCA` | **Ladder** *(inferred, strong)*. Every F3 face is a 1-1.5 m wide vertical strip exactly where a ladder is drawn: Field_02M cliff ladder (x -2.75..-1.75, y 16.5-23.3), Bottle Grotto 04D left/right ladders, Angler's Tunnel 07B ladder. 101 rooms. |
| 4 | 22 | 100% up | `0xD0A` | **Ladder top** *(inferred)*: a 0.5 m² landing directly behind the top of an F3 `0xCCA` field ladder, in the same 11 field rooms. |
| 5 | 5,442 | 100% up | `0x80140`, `0xD40`, `0xC0140` | **Pit / fall hazard (confirmed in code).** The hazard test at `0xD445D0` returns true for `F == 5` unconditionally. `0xC0140` is on `AbyssHole*` and `AbyssFallPlane`. |
| 6 | 12,884 | 100% up | `0x80180`, `0xD80`, `0xC0180` | **Pass-through overlay (confirmed in code).** When the ground query returns `F == 6` the player marks the query and repeats it (`0xD54514` -> `0xD54854`) to find the surface below. `0xC0180` is on `AbyssHole*` and `AbyssSlope*`. |
| 7 | 2,608 | 98% up | `0x1D8`, `0xDD8` (low6 = 24) | **Deep water (confirmed in code).** `0xDB19D0` maps `F == 7` to hazard type 1, and the state at `0xDB13F0` starts the `DiveInto_Water` effect for type 1. 22,066 area units in 127 field rooms. |
| 8 | 524 | 100% up | `0x20F` (low6 = 15) | **Lava (confirmed in code).** `0xDB19D0` maps `F == 8` to hazard type 2, which starts `DiveInto_Lava` (`0xDB151C`). Turtle Rock floors, 37 rooms. |
| 9 | 74 | 100% up | `0x24C` | Face Shrine 04C, Eagle Tower 01C/05G *(unknown)*. |
| 10 | 30 | 100% wall | `0x81A80` | `AirWallPiccolo` and Eagle Tower 07H invisible wall. |

`F` = 5 and 6 also appear on every `Abyss*` helper asset and in 194 / 184
dungeon rooms as small, flat, up-facing patches. The hazard classifier at
`0xDB19D0` returns 1 for `F == 7`, 2 for `F == 8` and otherwise falls back to
a state flag; its caller (`0xDB11D4`) stores the result at `state + 0x68` and
branches to the `DiveInto_Water` (type 1) or `DiveInto_Lava` (type 2) setup.
The `F == 7` and `F == 8` hazard checks at `0xD445D0` also depend on the
flag byte at `PlayerLink + 0x1786`, whose meaning is not yet known.

`F == 6` is excluded from the general hazard test (`0xD93BE0`) the same way
`F == 5`/`7`/`8` are included, confirming it is a deliberate "safe, transparent"
class rather than an oversight. It also gates a floating-point calculation at
`0xDCE014` (a reciprocal-square-root distance/speed computation) that looks
like a wake or ripple effect trigger, separate from the ground-query
pass-through at `0xD54854` *(inferred, not fully traced)* - `F == 6` may cover
floating debris/raft surfaces as well as plain overlays.

**Bit 19 (`0x80000`) and bit 18 (`0x40000`)** appear on the invisible-wall
assets `AirWallAutojump` (`0x80000`) and `AirWallAutojumpD` (`0xC0000`), and on
72% up-facing ground elsewhere. This is consistent with an auto-jump ledge
marker *(inferred; no code path found yet)*. `AirWallAll` / `AirWallAllOut` use
`0x81840`, `AirWallAbyss*` and `AirWallBG` / `AirWallEnt` use plain `0`.

**Bits 10-12 (`0xC00`, `0x1800`)** track area type: `0xC00` dominates outdoor
`Field_*` rooms, `0x1800` dominates houses, `0x0` dominates dungeons. They are
not read by the player code, so treat them as authoring/layer tags and preserve
them when copying a donor profile.

### Face-remap prelude

The surface-ID array is followed by a face-remap prelude in the ordinary
retail layout:

| Relative offset | Type | Description |
| ---: | --- | --- |
| `0x00` | `u32` | **Largest value in the remap** (`GuSerialize` `storeIndices`). Usually `T - 1`. |
| `0x04` | `remap_type[T]` | `mFaceRemap`: for each stored triangle, its face id in the original authored mesh. Element width follows the largest value: `<= 0xFF` → `u8`, `<= 0xFFFF` → `u16`, else `u32`. Independent of the triangle-index width. |

The largest value exceeds `T - 1` when PhysX's mesh cleaner removed
degenerate or duplicate faces before cooking; the remap then has gaps. The
cooker received the surviving faces in ascending face-id order, so sorting
the stored triangles by remap value reconstructs the exact cooking input. This
is what makes every retail mesh re-cookable (see `verify-cooker` below).

The next stable marker is the ASCII magic `RTRE`. The prelude is variable in
principle, so a parser should locate `RTRE` before the next `NXS\x01MESH`
record and validate the expected length rather than blindly assuming this
layout for an unknown file.

`mFaceRemap` is important to preserving a cooked collision mesh. Reordering,
adding, or deleting faces while merely copying it is unsafe.

## RTRE: PhysX R-tree

**The cooker does not need to be reverse-engineered - it is stock, open-source
NVIDIA code.** The retail `main` NSO's assert strings embed dozens of literal
build paths from the PhysX source tree, including the exact SDK tag:

```
PhysX-3_4_0-release
PhysX/PhysXSDK/Source/GeomUtils/src/mesh/GuRTree.cpp
PhysX/PhysXSDK/Source/GeomUtils/src/mesh/GuBV4.cpp
PhysX/PhysXSDK/Source/GeomUtils/src/mesh/GuBV4Build.cpp
... (30+ more GeomUtils/LowLevel* paths)
```

This is **PhysX 3.4**, whose source (including the cooking-time R-tree
builder, `GuRTreeCooking.cpp`/`.h`, alongside the runtime-side `GuRTree.cpp`
seen above) is published under a BSD license at
`github.com/NVIDIAGameWorks/PhysX-3.4`. The header fields below (4 nodes per
page, `be_f32` quantized bounds, page/level counts) match that cooker's
`RTreePage`/`buildFromTriangles` output exactly, not a Grezzo-custom format.

### Cooker: verified byte-exact

`CidbTool`'s port of `RTreeCooking.cpp` (`cook-blender-physx`) reproduces
**every** retail tree. `verify-cooker` rebuilds each mesh's cooking input from
its face remap, cooks it, and compares the stored triangle order, every RTRE
page and the RTRE header. Results: 4,499/4,499 map meshes and 158/158 actor
meshes byte-identical. `verify-recook` rebuilds whole containers through the
cook path: 1,052/1,052 map and 122/122 actor CIDBs byte-identical.

The settings and details that matter, all confirmed by that corpus check:

* **Branch and preset:** SAH branch (`PxMeshCookingHint::eSIM_PERFORMANCE`)
  with preset 8, i.e. PhysX's default `meshSizePerformanceTradeoff = 0.55`
  (`floor(0.55 × 15) = 8`: 32 triangles per terminal page, 5 per leaf). The
  QuickSelect branch matches almost nothing.
* **Cooked on a PC.** The surface-area metric `PxSAH = V3Dot(v, V3PermZXY(v))`
  must use the **x86 SSE** evaluation order, `y·x + (x·z + z·y)`. The ARM NEON
  order `(x·z + y·x) + z·y` or a naive `x·y + y·z + x·z` flips near-ties and
  reorders triangles (64–91% match instead of 100%). The split metric
  `countL·metricL + countR·metricR` uses two ordinary float roundings (no FMA).
* `split()` returns immediately for clusters of 1 or 2 without re-sorting, and
  `sort4` never calls `split()` for clusters smaller than 4 (fixed split
  positions `{1, 2, 3}` clamped to the size).
* A split whose triangle areas differ by 40× or more clears the "terminal
  page" flag **during** the slot loop, affecting that slot and later slots.
* Empty node pointer: `(first_empty_node_index × 28) | 1` (`sizeof(RTreeNodeQ)`
  = 28, leaf bit set), not `0`.
* Ties in the initial per-axis sorts use PhysX's own `Ps::sort` (median-of-3
  quicksort, selection sort below 5 elements), already ported literally.

`RTRE` begins immediately after the face-remap prelude. It uses **big-endian**
integers and floats, unlike the surrounding NXS mesh data.

### Header

The header is `0x60` bytes long.

| Offset | Type | Description |
| ---: | --- | --- |
| `0x00` | `char[4]` | Magic: `RTRE` |
| `0x04` | `be_u32` | Version. Reader accepts retail values `1` and `2`. |
| `0x08` | `be_f32[4]` | Cooking bounds minimum `(x, y, z, 0)`. |
| `0x18` | `be_f32[4]` | Cooking bounds maximum `(x, y, z, 0)`. |
| `0x28` | `be_f32[4]` | Observed inverse diagonal `(1, 1, 1, 1)`. |
| `0x38` | `be_f32[4]` | Observed diagonal scaler `((max-min) / 65535, 0)`. |
| `0x48` | `be_u32` | Nodes per page. Observed value: `4`. |
| `0x4C` | `be_u32` | Root-page count. |
| `0x50` | `be_u32` | Tree level count. |
| `0x54` | `be_u32` | Total node count. Observed as `page_count * 4`. |
| `0x58` | `be_u32` | Page count (`P`). |
| `0x5C` | `be_u32` | Observed as `0`; purpose unknown. |

The cooking bounds are not the same thing as the current runtime mesh AABB.
For a same-topology vertex edit, the tested refit updates page bounds and the
mesh AABB but preserves the cooking header.

### Pages and nodes

`P` pages follow at `RTRE + 0x60`. Each page is `0x70` bytes and contains four
nodes in a structure-of-arrays layout.

| Page-relative offset | Type | Description |
| ---: | --- | --- |
| `0x00` | `be_f32[4]` | Minimum X for slots 0–3 |
| `0x10` | `be_f32[4]` | Minimum Y for slots 0–3 |
| `0x20` | `be_f32[4]` | Minimum Z for slots 0–3 |
| `0x30` | `be_f32[4]` | Maximum X for slots 0–3 |
| `0x40` | `be_f32[4]` | Maximum Y for slots 0–3 |
| `0x50` | `be_f32[4]` | Maximum Z for slots 0–3 |
| `0x60` | `be_u32[4]` | Child/leaf pointer for slots 0–3 |

A slot whose minimum X is `float.MaxValue` is an empty sentinel.

For a non-empty node pointer `p`:

| Test | Meaning |
| --- | --- |
| `p & 1 != 0` | Leaf. `first_triangle = p >> 5`; `triangle_count = ((p >> 1) & 0xF) + 1`. The leaf references a contiguous range in the stored triangle order. |
| `p & 1 == 0` | Child page. `p` is a `0x70`-aligned byte offset from the start of the RTRE page array. |

Retail child pages occur later than their parent in the packed page array.
This permits a safe bottom-up refit.

## Mesh tail

The tail begins after the RTRE header and all pages:

```text
tail_offset = rtre_offset + 0x60 + (P * 0x70)
```

| Tail offset | Type | Description |
| ---: | --- | --- |
| `0x00` | `f32` | PhysX `mGeomEpsilon`: geometry/cooking tolerance stored with the mesh. The baker preserves its exact bit pattern from the template mesh. |
| `0x04` | `f32[3]` | Current mesh AABB minimum `(x, y, z)`. Computed like `Gu::computeBoundsAroundVertices`: start from the **last** vertex, then fold vertices `0 … n-2` with SSE `min`/`max`, which keep the new value on a tie. This decides the sign of zero (`0.0` vs `-0.0`) and is required for byte-exact output. |
| `0x10` | `f32[3]` | Current mesh AABB maximum `(x, y, z)`. |
| `0x1C` | `u32` | Triangle count; matches `T`. |
| `0x20` | `u8[T]` | PhysX `mExtraTrigData`, one byte per triangle. It carries active-edge/collision-edge flags, not the water surface material. |

In the audited retail meshes, this byte uses only bits `0x08`, `0x10`, and
`0x20`, in all combinations from `0x00` through `0x38`. This matches the
PhysX 3.4 cooker source: it assigns `ETD_CONVEX_EDGE_01`,
`ETD_CONVEX_EDGE_12`, and `ETD_CONVEX_EDGE_20` to the three directed triangle
edges when they are active collision features. In other words, the byte
controls whether triangle edges participate in contact generation; it is not a
candidate footstep-material selector. See the
[PhysX 3.4 triangle-mesh cooker](https://github.com/NVIDIAGameWorks/PhysX-3.4/blob/master/PhysX_3.4/Source/PhysXCooking/src/mesh/TriangleMeshBuilder.cpp).

### Active-edge flags: verified rule

The flags are pure derived data. `CidbTool` ports PhysX 3.4
`EdgeListBuilder` (`createFacesToEdges`, `createEdgesToFaces`,
`computeActiveEdges`, `Epsilon = 0.1` rad) and the replication in
`TriangleMeshBuilder::createSharedEdgeData`; `verify-edges` reproduces the
retail byte for **all 4,657 meshes / 317,126 triangles (100%)**.

The rule runs on the **stored** (post-R-tree) triangle order and the stored
vertex indices (no position welding):

* Edge = unordered vertex-index pair. Triangle edge 0 = (v0,v1) → `0x08`,
  edge 1 = (v1,v2) → `0x10`, edge 2 = (v2,v0) → `0x20`.
* Edge used by **one** triangle (open border): active.
* Edge shared by **two** triangles `f0 < f1`: take the vertex of `f0` opposite
  the edge; if it lies below `f1`'s plane (`distance < 0`, a convex fold),
  the edge is active when the angle between the two normals is > 0.1 rad
  (≈5.7°). Otherwise (flat or concave) it is active only if the faces are
  folded back onto each other (`dot(N0,N1) < -0.999`).
* Edge shared by **more than two** triangles: count distinct triangles
  (same vertex set = duplicate). 1 → active; 2 → same test as above (angle
  only, if one is a back-face duplicate); 3+ → active.

Consequence for authoring: flat floors have inactive inner edges, while
borders, convex ledge lips and walls' outer corners are active (the player
collides with/slides off these). Concave corners (floor meeting wall) are
inactive. The cooker now recomputes the byte for every face, so newly drawn
geometry gets retail-correct flags and the `_extra_` value in OBJ material
names is ignored on the PhysX path.

## Editing rules

| Change | Safe current route | Why |
| --- | --- | --- |
| Only `surface_id[i]` | Rewrite the relevant `u16`s; preserve all other bytes. | The mesh and R-tree remain byte-identical. |
| Vertex positions, same topology | Rewrite vertices; refit existing leaf and ancestor bounds; update the tail AABB. | Leaf triangle ranges and retail page layout remain valid. |
| New connectivity with identical per-mesh counts | Experimental template-tree route. | Donor leaf ranges and page layout can be retained, but gameplay needs testing. |
| Added/deleted/reordered faces or vertices | `cook-blender-physx` (preset 8). | The cooker is proven identical to Grezzo's on every retail mesh, and a decoded vanilla room cooked back from its Blender OBJ is byte-identical. Edge flags are recomputed (verified on every retail mesh), and `cidb_contact_<hex>` faces get their full contact code. |

For unchanged topology, keep the collision-mesh boundaries, vertex order,
triangle order, winding, face remap, surface IDs, extra-triangle bytes, and
RTRE layout unless that specific field is the subject of the edit.

## Tooling

[README.md](README.md) lists every CidbTool command;
[blender_addon/README.md](blender_addon/README.md) covers the Blender add-on,
which imports meshes and primitives, names each face after its retail preset
(`LAS Stone · Dungeon`, `LAS Deep water (Swamp) · Dungeon`, …) and bakes
through `cook-blender-physx`.

## Open questions

* Gameplay meaning of `F` = 1, 3, 4, 9 and 10 (geometry and asset-name evidence
  only; F3/F4 are very likely ladder / ladder top). Test plan with exact spots:
  [INGAME_TESTS.md](INGAME_TESTS.md). `F` = 5, 6, 7 and 8 are confirmed by
  code (fall hazard, pass-through, deep water, lava).
* Meaning of the flag byte `PlayerLink + 0x1786` that gates the `F == 7` and
  `F == 8` hazard checks (`0xD445D0`), and of the global byte tested at
  `[0x1CC1368] + 0xAC`.
* The `0xDCE014` wake/ripple-looking calculation gated by `F == 6` - not fully
  traced, may indicate floating/raft surfaces as a sub-case of `F == 6`.
* Whether index 0 ("no special material") really plays GRASS footsteps on
  bare cave floors, or whether another path suppresses the sound; the SE
  builder does pick a per-action sub-path for action indices 7-9 (the
  WALK/RUN/LAND duplicates) that hasn't been traced yet either.
* Meaning of descriptor `+0x0C` high `u16` (group number) and of mesh kind
  `1` vs `2` at `+0x10`.
* In-game validation of new topology near water/lava/pit surfaces. The cooked
  bytes are now provably retail-equivalent, so this checks gameplay data
  (contact codes, `F` class), not the tree.
