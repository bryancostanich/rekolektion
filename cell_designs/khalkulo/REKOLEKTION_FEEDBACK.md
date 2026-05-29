# REKOLEKTION_FEEDBACK — cumulative findings from cell-build loop

## Helper bugs

### `pin_patch` paints redundant mcons
The 1.8 V FET primitives (`nfet_01v8_*`, `pfet_01v8_*`) already paint mcons at every S/D and gate li1 contact (verified by grepping the primitive `.rkt`). `pin_patch` paints an additional mcon at the pin center, which lands within `mcon.2` spacing (190 nm) of the primitive's existing mcons. Worked around in `build_nand2_inv_lv.py` by stripping `mcon_rects` after the call.

**Recommendation**: `pin_patch` should detect and skip the mcon when the primitive already contacts li1→met1 at that pin. Alternatively, expose a `mcon=False` keyword argument.

### `pin_patch` paints redundant met1 with insufficient sizing for via.5a wide-axis
The 1.8 V FET primitives also paint their own met1 strips (S/D 230 nm wide, gate 290 nm wide). `pin_patch` paints a 320×320 met1 patch by default, which is correct for via1 enclosure (≥30 nm narrow / ≥60 nm wide). But:
1. At gate pins (NMOS y=620, S/D primitive met1 top y=345), the centered 320×320 patch puts patch bottom at y=460, gap to S/D primitive met1 top = 115 nm < 140 nm met1.2 spacing.
2. Adjacent pins at 220 nm pitch (gate vs S/D within a single FET) get patches that overlap if both 320 wide.

Worked around by allowing per-pin y-offset (gate patches shifted ±25 nm).

**Recommendation**: `pin_patch` should be aware of the surrounding primitive met1 geometry and shift/resize the patch to avoid spacing violations. At minimum, expose `patch_x_offset` and `patch_y_offset` keyword args.

### `place_via` upper-layer enclosure is too generous for tight stdcell pitches
`place_via((x, y), "met1", "met2")` paints an 85-nm met2 enclosure each side → 320 nm wide met2 rect. For a 220-nm pin pitch (gate vs S/D in 01v8 primitives), two via1 stacks at adjacent pins produce overlapping met2 rects → met2.2 short across nets.

The minimum legal via1 met2 enclosure is 55 nm wide-axis + 40 nm narrow-axis (asymmetric). 220×140 met2 patch would fit at 220 nm pin pitch.

**Recommendation**: `place_via` should accept `up_encl_um=(narrow, wide)` to allow caller to opt into a tighter (rule-minimum) enclosure when geometry is constrained. Or expose a `min_encl_um` argument.

## Helper gaps

### No multi-net router with channel allocation
The current routing helpers (`place_wire`, `place_via`, `route_net_on_track`) treat each net independently. There's no facility to:
- Allocate distinct Y bands for parallel horizontal tracks
- Allocate distinct X columns for parallel vertical tracks
- Detect and avoid collisions between nets sharing a layer
- Choose which layers to use for which nets based on density

The previous session's `route_net_on_track` attempt produced 175 DRC errors at the nand2_inv_lv cell (collisions between the trunk and branches of the same net's via1 stacks).

### No sref-reflection helper / std-cell idiom builder
For digital cells like NAND2+INV, the canonical layout pattern is FET abutment with shared S/D li1 strips, which would eliminate the intra-column gate-vs-S/D collision problem. This requires SRef reflection (orient certain FETs with D on right instead of left) so abutting FETs share the right diff strip with the next FET's left diff strip. The `rkt.SRef.reflect` field exists but no helper composes it correctly with primitives' label positions and bbox.

### `inspect_primitive` rejects composite cells
`inspect_primitive` raises `MultiCellPrimitiveError` when the cell's child SRefs are at non-zero origins. This breaks any attempt to SRef a hand-authored block (like `nand2_inv_lv.rkt` or `lshift_1v8_to_3v3.rkt`) into a parent cell — the parent can't introspect bbox/pins of the child block to compute placement origins.

Worked around in `build_cim_reram_drv_phaseA_srcmux.py` with hardcoded bbox tuples. **Recommendation**: extend `inspect_primitive` to compute bbox by walking child SRefs and accumulating their bboxes (translated by their origin), or expose a separate `inspect_block` function.

### No "intra-column" spacing analysis
When dropping multiple via1 stacks at pins within a single FET column (e.g. gate at x=600 and drain at x=380, separated by 220 nm), the helpers have no awareness that the resulting met1/met2 enclosure rects will overlap. A precondition check (`would_collide(via_a, via_b, layer="met2")`) would catch this before DRC.

## "Obvious mistake" catalog additions

1. **Patch + primitive met1 overlap creates met1.2 spacing fail with NEIGHBOUR primitive met1.** The primitive paints met1 at S/D and gate. A pin_patch at one pin (gate) extends in y past the primitive's gate strip. If the patch bottom lands within 140 nm of an adjacent S/D primitive met1 strip top, met1.2 fails. **Catch via `viz read` the FET primitive — note its met1 y-extents — and verify your patches don't violate clearance to those.**

2. **Adjacent via1 stacks at intra-column pins (220 nm pitch in 1.8 V primitives) ALWAYS have met2 enclosure collisions** with default 320-nm-wide enclosures from `place_via`. Visual signal: in the rendered PNG, two yellow met2 enclosure rects touching with a thin sliver of background between (or no gap). **Verify pin-pitch ≥ pitch_required = encl_a_half + spacing(140) + encl_b_half before assuming a column has space for two via1 stacks at adjacent pins.**

3. **Trunk wires must extend ≥30 nm + 75 nm = ~105 nm past their end via1 cuts** to satisfy via.5a met1 enclosure on the outer side. `TRUNK_OVERHANG=100` is JUST short; use 130+ to be safe.

4. **Via2 met2 enclosure depends on via1's upper rect being ≥45 nm bigger than via2's cut on each side.** Stacking via1+via2 at the same pin works only if via1's met2 enclosure (320 wide) is enough for via2 cut (200 wide) + 60 nm = 320. Bare minimum, no margin. If the pin's via1 met2 rect MERGES with an adjacent same-layer polygon (different net), Magic's via2.4a check sees the merged-polygon edges and may flag the via2 enclosure incorrectly — actually a real short.

5. **Y column at gate-x and gate via1 met2 enclosure at adjacent column edge can SHORT.** When the cross-row signal column wire (e.g. Y at INV_N.D x=3780, met2 wire 140 wide → x=3710..3850) and an adjacent via1 met2 enclosure (e.g. INV_N.G at x=4000, met2 enclosure 320 wide → x=3840..4160) overlap (here by 10 nm), DRC flags via2.4a (because of merged polygon shape) but the real bug is a met2 short between two different nets. **Visual catch: run `viz app`, switch to Nets tab, confirm no two nets share a polygon.**

## Recurring patterns of mistakes

1. **Helpers paint redundant geometry that primitives already provide.** Both `pin_patch` (mcon) and the patch's met1 fall in this bucket. The primitives ARE already production-quality; treating them as "raw silicon" and painting on top creates conflicts.

2. **Default helper enclosures are sized for "typical" analog pitches, not std-cell tight pitches.** 320×320 met1 patches and 320×320 met2 enclosures are fine for a 1-µm-pitch analog block but cripple a 220-nm-pitch digital cell.

3. **`route_net_on_track` doesn't track inter-net collisions.** Each net's track Y or X is allocated independently; collisions between nets aren't detected. For multi-net cells with 5+ nets, hand-routing with `place_wire` + `place_via` is the only safe path today.

---

## Run 2 findings (3 helper fixes available: ac37c7b)

### Wins
- **`inspect_primitive(path, search_dirs=[...])` for composite blocks now works perfectly.** Eliminated the hardcoded bbox tuples in `build_cim_reram_drv_phaseA_srcmux.py`. Now reports actual composite extent (-355, -1640, 4955, 6250) for nand2 — accounts for rail/tap extension that the hand-computed (-155, -1500, 4755, 5755) missed. Resulting srcmux placement: identical density, cleaner code.
- **`pin_patch(mcon=False)` semantics are correct.** No redundant mcon stacking observed in any build.

### Limits of asymmetric `place_via(up_encl_um=(x, y))`
Asymmetric upper enclosure is necessary but **not sufficient** for stdcell-pitch routing. Concrete observation from nand2_inv_lv iter 15:

- Default symmetric (320×320 nm): 10 met2.2 errors at intra-FET pitch
- Asymmetric (220×340 nm) with `up_encl_um=(0.055, 0.085)`: **74 met2.2 errors** — WORSE

Root cause: asymmetric (0.055, 0.085) is **wider** on the y-axis (340 nm tall) than symmetric default (320 nm tall). The trunk-end via1s sit inside a 320-tall met1 trunk; their 340-tall met2 enclosure rects stick 10 nm past trunk top/bottom on both sides, creating new met2.2 violations against same-x metal on adjacent rows.

The asymmetric encl is useful in **one axis only** — when you can guarantee the rect won't grow in the other axis. The helper would benefit from a 4-tuple `(xL, xR, yB, yT)` form so callers can opt into shrinkage on the tight axis WITHOUT growth on the loose axis. Or pass `(x_encl, y_encl)` where both axes can be set **below** the symmetric default — currently the y-direction defaults to `default_up_encl_um` (85 nm) when only x is passed... actually no, the tuple form requires both. So caller must always shrink y too.

**Recommendation**: `place_via` should accept `up_encl_um=(0.055, 0.060)` where 0.060 = absolute minimum y-encl (40+ rule). Then both axes are narrower. But the SKY130 rule says one axis ≥40 nm AND the other ≥55 nm asymmetric — both can't be 40 simultaneously. Need rule-aware encl picker that minimizes BOTH axes given the asymmetric constraint.

### Architectural ceiling — confirmed at multiple cells
nand2_inv_lv (10 met2.2), lshift_1v8_to_3v3 (6 met1.6 + signal routing not attempted), and srcmux/phaseA (inherit subcell errors) ALL hit the same wall: **the 1.8 V FET primitive's 220 nm intra-pin pitch (gate centerline to S/D centerline) makes it geometrically impossible to host two via1 stacks at adjacent pins on the same metal layer**.

Even minimum-rule met1/met2 enclosures (40-55 nm) give via1 met2 rects 230-260 nm wide. Two adjacent rects at 220 nm pitch overlap with 10-40 nm of overlap before considering the 140 nm met2 spacing rule.

The 3 fixes from ac37c7b are NECESSARY for any future stdcell routing and they DO unlock composite composition (the srcmux is cleaner now). But by themselves they don't lift the architectural ceiling on multi-net stdcell routing.

### Top 3 helper gaps remaining

1. **No SRef-reflection + abutment helper for stdcells.** Std-cell row layouts handle the intra-FET pitch problem via shared S/D li1 strips that abut across FETs (eliminating the "expose gate AND S/D from same FET" problem entirely). Without this, every digital stdcell in the project will hit the same wall.

2. **No "intra-column collision check" in place_via.** When dropping a via1 stack near another via1 stack, the helpers should validate the met2 enclosure rects don't collide (and refuse to paint, or warn). Today this is silent → 100+ DRC errors per cell when wrong.

3. **No `pin_patch` y-offset / asymmetric-patch parameter.** The 320×320 patch lands within 140 nm of adjacent primitive S/D met1 strips for LV FETs. Workaround uses a hand-defined `met1_patch` helper that shifts y by ±25 nm. `pin_patch` should learn about adjacent primitive geometry and shift/shrink automatically — or accept `patch_x_offset`, `patch_y_offset`, `patch_half_um=(x_half, y_half)`.

### Top 3 recurring mistakes (re-confirmed run 2)

1. **Helpers paint redundant primitive met1 → spacing violations against primitive geometry.** Same pattern as in run 1. `pin_patch(mcon=False)` fixes the mcon side but not the met1 side.

2. **Trunk + branch via1 met2 enclosures collide with cross-row column wires at intra-FET pitch.** Cannot be fixed by enclosure tuning alone.

3. **Asymmetric encl can grow one axis while shrinking the other.** If the design has constraints on both axes, naive asymmetric application makes it worse.

### Recommended next-session priorities

1. **Build a stdcell-abutment helper** that accepts a row of primitives, computes the SRef.reflect orientations for shared S/D, and emits the correct origins. Even a minimal version that handles NAND2/INV/NOR2 unlocks the entire digital block layer of the chip.

2. **Add a primitive variant `*_polyend` with elongated gate poly** that exposes the gate contact outside the FET diff envelope (e.g., at x=COL+700 instead of COL+0). This decouples gate routing from S/D routing on the same FET column.

3. **Implement `place_via_safe(point1, point2, ...)` that takes ALL via1 stacks in a region and picks asymmetric enclosures + jog directions to avoid mutual collisions.** Then designers describe pins and the helper figures out enclosure shapes per-pin.

---

## Run 3 findings (2026-05-28) — ToGds `IsInternal` doc/impl drift

### Bug: `internal=True` labels were exported to GDS

`tools/viz/src/Rekolektion.Viz.Core/Rkt/ToGds.fs:187` unconditionally emitted every `LabelEl` as a GDS text record:

```fsharp
| LabelEl l -> [ Gds.Types.Text (labelToGds l) ]
```

The `Label` type's own docstring (`Rkt/Types.fs:151-156`) states the opposite contract: *"`IsInternal = true` marks the label as a viz/debug annotation that **ToGds.fs deliberately skips when exporting GDS**. Magic's `port makeall` only sees GDS text records, so internal labels never become subckt ports during LVS extraction. Used for naming internal nets without breaking LVS."*

The bug had been silently breaking LVS on every cell that used `internal=True` to name internal nets. Magic was promoting these internal labels to subckt ports → layout `.subckt` lines had extra ports → LVS reported "Netlists match uniquely with port errors."

### Fix

Single-line gate at `ToGds.fs:187`:

```diff
- | LabelEl l -> [ Gds.Types.Text (labelToGds l) ]
+ | LabelEl l -> if l.IsInternal then [] else [ Gds.Types.Text (labelToGds l) ]
```

### Impact

Cells that gate-3-cleared from this one fix:

| Cell | Pre-fix LVS | Post-fix LVS |
|---|---|---|
| `lshift_1v8_to_3v3` | Port mismatch (extra `IN_n` port) | **MATCH** |
| `nand2_inv_lv` | Port mismatch (same `IN_n`-style leakage) | **MATCH** |

`nand2_inv_lv` still has 12 met2.2 DRC errors from the 220 nm intra-FET pitch ceiling (separate architectural problem — needs abutment helper or `*_polyend` primitive variant), but its topology was correct all along.

### Regression check

- `blc_trim_dac` — DRC clean, LVS mismatch. Pre-existing layout bug (pfet gate not wired to `SIGN` net, sits on orphan auto-named subckt-pin). Not caused by the fix; suppressing labels can't disconnect a net.

### Lesson

**Type docstrings that name a sibling file's behavior must be audited against that file.** The `Types.fs` doc literally said *"ToGds.fs deliberately skips"* — a sentence that was always wrong from the day it was written. The contract was respected by build-script intent (`internal=True` callers were already in place) but never by the converter. Doc/impl drift in the type's own documentation is particularly insidious because reviewers see the contract and trust it.

Worth a one-time audit of every `Types.fs` doc reference to a sibling module behavior, and a future-proofing test: `ToGds(Label(internal=True))` should produce zero `Gds.Types.Text` records.

