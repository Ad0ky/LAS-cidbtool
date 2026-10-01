# In-game behaviour tests for unconfirmed collision classes

Purpose: confirm what the still-unknown parts of the contact code do, so the
Blender preset library can give them final names. Everything else (footstep
materials 1/5/7/10/12/21/23/38/41, classes F5 pit, F6 pass-through, F7 deep
water, F8 lava) is already confirmed.

How to read the table
* Contact code: `F` = bits 6-9 (behaviour class), `low6` = bits 0-5 (surface).
* Spot = an open, visible point inside the matching triangle (not covered by
  trees or props), in raw game coordinates (X, Y = height, Z). In Blender after
  **Import LAS CIDB Collision** the same point is (X, Z, -Y). The face is face
  `<tri>` of object `collision_mesh_<mesh>` (stored triangle order = Blender
  face order). Face numbers are for the plain room file, not its `Panel…`
  copy (those can differ).
* To find a spot, import the room's CIDB with the Blender add-on next to the
  room model and go to the listed face, or place the 3D cursor at (X, Z, -Y).
* Do each test once on the target surface and once on a normal neighbour
  surface in the same room, so you can tell the difference.

| # | What we test | Room | Mesh / face | Code | Spot (X, Y, Z) | Where it is | Hypothesis to check |
|---|---|---|---|---|---|---|---|
| 1 | F1 (166k triangles, most common unknown) | Field_06C | 1 / 0 | `0xC43` | (-5.73, 13.5, -3.55) | Left tree ledge, 0.75 m above the clearing, between the trees | F1 is mostly walls; here it is the raised ground under the tree line. Can Link stand on it, or is he pushed off? Footsteps? |
| 1b | F1 second sample | Field_07A | 1 / 0 | `0xC43` | (-1.23, 13.5, -2.05) | Tree ledge at the right edge of the trees | Same as 1 |
| 2 | F3 (740 triangles, 101 rooms) | Lv04AnglersTunnel_07B (side view) | 2 / 56 | `0xCC` | (5.0, 5.0, -1.25) | The ladder next to the right tower; the F3 strip covers x 4.5–6, y 1.5–12 | **Ladder.** Every F3 face in the game is a 1–1.5 m wide vertical strip facing ±Z exactly where a ladder is drawn. Check that Link climbs here, and test whether painting F3 on a plain wall makes it climbable |
| 2b | F3 second sample | Lv02BottleGrotto_04D (side view) | 2 / 18 | `0xCC` | (-5.0, 8.5, -1.25) | Left ladder (another F3 strip is the right ladder) | Same as 2 |
| 3 | F4 (22 triangles game-wide) | Field_02M | 2 / 66 | `0xD0A` | (-2.22, 23.26, 5.70) | 0.5 m² landing at the top of the wooden cliff ladder; the ladder itself is F3 `0xCCA` | **Ladder top.** Does Link climb off the ladder onto this patch? Walk onto it from above: does he start climbing down? |
| 4 | F9 (74 triangles) | Lv06FaceShrine_04C | 1 / 9 | `0x24C` | (2.11, 1.5, -4.92) | Top row of square tiles, right of centre, against the purple wall | Raised (y 1.5) band next to the wall. Stand / walk / drop a pot: sliding, ice, damage, special sound? Is it reachable at all? |
| 4b | F9 second sample | Lv07EagleTower_01C | 1 / 5 | `0x24C` | (-1.62, 1.5, -4.92) | Raised alcove at the top wall (left edge) | Same as 4 |
| 5 | F10 (invisible wall) | Lv07EagleTower_07H | 3 / 32 | `0x81A80` | (1.0, 1.0, -3.0) | Front edge of the raised alcove at the top wall (x -3..3, height 0–1.5) | Blocks Link? Thrown items, arrows, boomerang, the iron ball, enemies? (also used by actor `AirWallPiccolo`) |
| 6 | Surface 0 (`low6 = 0`, table says GRASS) | Field_08K | 1 / 0 | `0x0` | (-4.54, 8.25, -4.64) | Top of the house's front wall, behind the stone rim and in front of the flower box | Probably not walkable in normal play. If you can get up (or move Link there), check the footstep sound. Otherwise skip; surface 0 is also on most plain walls |
| 7 | Bit 19 `0x80000` on ground | Field_06J | 1 / 21 | `0x80C0C` | (-3.27, 11.25, 1.77) | Lower diamond-tile floor, just left of the left small tower | Same surface as normal stone tile `0xC0C` plus bit 19. Any difference walking off its edge (auto-jump)? No code reading this bit was found, so "no difference" is a valid result |

Notes
* Bit 18 `0x40000` appears only on the `AirWallAutojumpD` actor, never on room
  ground, so it is not testable from room collision.
* Edge auto-jump in this game comes from placed level actors
  (`AirWallAutojump`, `AirWallAutojumpD`), not from a floor material. Test 7
  checks whether room collision contributes as well.

Record results here
| # | Result | Date |
|---|---|---|
| 1 | | |
| 2 | | |
| 3 | | |
| 4 | | |
| 5 | | |
| 6 | | |
| 7 | | |
