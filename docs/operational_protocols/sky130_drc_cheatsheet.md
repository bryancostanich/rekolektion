# SKY130 DRC / LVS cheatsheet

Quick reference for rules, thresholds, and Magic/netgen behaviors that
tripped us up while hand-authoring `.rkt` blocks. Not a replacement
for the PDK rule deck — just the gotchas worth keeping at hand.

## Magic DRC mechanics

### DRC coords are in 5 nm units

Magic's `drc listall why` reports tile coords in its internal grid,
which for SKY130 is **5 nm per unit**. To correlate with `.rkt`
coordinates (nm): **multiply Magic coord × 5**.

```
Violation at x=5874..5875, y=-1108..-1097
  → global x=29370..29375, y=-5540..-5485 (nm)
```

### `Magic doesn't honor LabelKind from .rkt`

The `.rkt` format carries `(kind net-name)` / `(kind port-name)` /
`(kind device-terminal)` annotations on labels. **These are stripped
when the block is exported to GDS** for Magic's input. From Magic's
perspective every label on a port layer (`met1_label`, `li1_label`,
etc.) is a candidate port and gets promoted to the subckt port list.

**Practical implication:** at the parent level (the block whose
ports you care about for LVS), only paint labels for nets that
should be subckt ports. **Don't add labels for internal nets** —
Magic auto-traces connectivity from geometry and assigns its own
internal name. Adding internal-net labels at the parent creates
spurious extra ports.

### `place_via` paints upper-layer pad only

The `place_via(point, from, to)` helper emits the cut + the
upper-layer enclosure pad. The lower-layer met1/met2 must be drawn
explicitly by the caller. The auto-emitted pad uses each via's
**default symmetric enclosure** (e.g. 85 nm for via1) — fine for
upper-layer enclosure but undersized to satisfy the *next*
via's lower-layer enclosure when stacking via1 → via2.

Standard practice:
- via1 site: explicit `m1_pad` (320 nm square, half=160) below + auto met2 pad above.
- via1 → via2 stack: also explicit `m2_pad` (370 nm square, half=185) to satisfy via2's met2 lower enclosure.
- via2 → via3: explicit `m3_pad` (390 nm square, half=195).

## Rule thresholds that bit us

### `met1.6` min area — use 0.090 µm² safe, not 0.083

Documented threshold is 0.083 µm² (83 000 nm²). Empirically a
290 × 287 = 83 230 nm² pad still flags. The FET generator's
`_fix_met1_min_area` pass uses **90 000 nm²** as the internal
threshold for safety.

### `via.5a` asymmetric enclosure — bump past 30 nm threshold

Asymmetric enclosure rule allows ≥85 nm in one direction with ≥30 nm
on other sides. A pad with **exactly 30 nm** short-side enclosure
can still flag on edge tiles. **Use ≥40 nm minimum** for the short
side; 50 nm is comfortable.

### `met1.2` / `met1.1` — step-corners trigger width violations

When two met1 rects with different x extents abut at one y (e.g., a
wide pin strip continuing as a narrower routing vertical), Magic's
tile algorithm can report a `met1.1 width < 0.14 µm` tile **at the
step**, even though both contributing rects are individually ≥140 nm.

**Fix:** extend the narrower rect to overlap the wider one (in
addition to touching). The polygon becomes a smooth L instead of a
step, and the tile algorithm finds no narrow tile.

```python
# Bad — narrow rect just abuts wide rect at y=Y_TOP:
Rect(narrow_x1, y_bot, narrow_x2, Y_TOP)   # 140 wide
Rect(wide_x1,   Y_TOP, wide_x2,   y_top)   # 230 wide

# Good — narrow rect extends a bit past the boundary:
Rect(narrow_x1, y_bot - overlap, narrow_x2, Y_TOP)
Rect(wide_x1,   Y_TOP - overlap, wide_x2,   y_top)
```

### Diffusion taps — `licon.7` and `li.5` need bigger enclosures

For n+ (or p+) tap contacts the enclosure rules are tighter than
regular S/D contacts:

| Rule    | Spec                                         | Minimum encl |
|---------|----------------------------------------------|--------------|
| licon.7 | n-tap diff overlap of n-tap contact          | 0.12 µm in one direction |
| li.5    | li1 overlap of diff-contact                  | 0.08 µm in one direction |

For a 170 × 170 nm licon1, that means diff ≥ 410 nm tall (or wide,
in the one direction) and li1 ≥ 370 nm in the same direction.
**Standard 60 nm "regular" enclosure fails both rules** for tap
contacts.

Body-tap layer stack pattern (parent paint, 1.8 V PFET into VDD nwell):

```python
# nwell extension — same bias as cell, abuts cell's intrinsic nwell
Rect("nwell",  x1, y1, x2, y2)
# n+ implant select — ≥125 nm encl of diff
Rect("nsdm",   x1 - 125, y1 - 125, x2 + 125, y2 + 125)
# n+ active — ≥120 nm encl of licon in one direction
Rect("diff",   cx - 150, cy - 205, cx + 150, cy + 205)
# Contacts and stack to met1
Rect("licon1", cx - 85,  cy - 85,  cx + 85,  cy + 85)
Rect("li1",    cx - 145, cy - 185, cx + 145, cy + 185)
Rect("mcon",   cx - 85,  cy - 85,  cx + 85,  cy + 85)
Rect("met1",   cx - 160, cy - 140, cx + 160, cy + 140)
# Route met1 to the bulk net (VDD/VSS/etc.)
```

### `diff/tap.3` — 270 nm n+ ↔ p+ inside the same nwell

This rule makes body taps physically impossible to fit *inside* a
tight PFET cell footprint — the available space between p+ S/D diff
and nwell edge is typically <100 nm, and even less when you account
for nwell encl of diff. **Solution:** extend the nwell into a free
area outside the cell footprint (same bias, so they merge as one
node) and place the tap there.

### `nwell.2a` — 1.27 µm spacing between distinct-bias nwells

When a sub-cell needs a body bias different from the surrounding
nwell strip (e.g., a 1.8 V PFET amid VDDA1=3.3 V strips), the strips
must keep ≥1.27 µm from the cell's intrinsic nwell. Cut the strips
around the cell with that setback. If the strips connected an
earlier cluster of nwells, verify the cluster still merges through
*other* cells' internal nwells after the cut, or add a small bridge.

## LVS / netgen behaviors

### Multifinger devices need `nf` in the schematic

A wrapper subckt with `nf=N` internally extracts as `w_total = w_per_finger × N`.
Schematic must declare both, or netgen sees a width mismatch (e.g.,
extracted `w=250`, schematic `w=25`).

```spice
* Wrong — netgen sees w=25 vs extracted 250:
XPMOS  drain gate source bulk sky130_fd_pr__pfet_g5v0d10v5 w=25 l=0.5

* Right — schematic carries the true effective width + finger count:
XPMOS  drain gate source bulk sky130_fd_pr__pfet_g5v0d10v5 w=250 l=0.5 nf=10
```

### Body-tap-less primitives extract as floating nwells

The SKY130 PDK's `_core` (non-guard) 1.8 V FET primitives **do not**
include an internal body tap. In the extracted netlist, the bulk
becomes a floating node at the wrapper subckt level (a port like
`w_n109_n264#`), and LVS reports one extra net.

**Three options to make LVS clean:**
1. Parent-paint a body tap (see `licon.7`/`li.5` pattern above) and
   tie to the proper bulk net. *Standard fix.*
2. Use the `guard=True` primitive variant. Has a built-in body ring
   but the cell footprint roughly doubles in both dimensions —
   often won't fit a tight cluster.
3. Bias the cell's body to the surrounding strip's voltage (merge
   with adjacent nwell). Only acceptable if the resulting body bias
   doesn't push the device past reliability limits (e.g., 1.5 V
   reverse bias on a 1.8 V PFET adds Vth, shifts speed, may exceed
   bulk-junction spec).

## PDK / generator caveats

### `mos_draw` emits under-area gate pads on short-L 1.8 V devices

The PDK's draw proc produces a met1 gate landing pad that for
short-L (~0.15 µm) 1.8 V FETs is **290 × 230 = 66 700 nm² — below
met1.6**. The fix is in the `_fix_met1_min_area` post-processing pass
in `src/rekolektion/primitives/sky130/fet.py` (FET generator v3+).

**Always DRC each primitive standalone after a generator change**,
even if the PDK proc itself was untouched — a parameter change can
make a previously-passing geometry fall under area.

### Wrapper cells need the same care as primitives

Any block that uses `nfet_01v8` / `pfet_01v8` inherits the gate-pad
behavior; a standalone DRC of the wrapper may pass even when the
primitive doesn't (because the wrapper paints something on top of
the small pad). After regenerating primitives, **DRC every wrapper
that imports them**, not just the primitives themselves.

## Routing topology

### Long verticals → use a layer that doesn't share with power straps

If a signal/power vertical must traverse the full cell height on
met2, it will collide with horizontal met2 power straps at the same
layer. **Move the long run to met1**, and hop to met2 only at the
endpoints where it connects to the strap or another met2 feature.
Met1 over met2 (different layers) is a normal cross-layer crossing
with no DRC implication.
