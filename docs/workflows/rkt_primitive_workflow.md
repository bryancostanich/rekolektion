# Building cells with the `.rkt` workflow

**Audience: agents and humans authoring SKY130 layout in this repo.**

This is the canonical way to build layout from now on. The old
`source/cim/layout_gen_sky130b.tcl` workflow (Magic TCL composing
`.mag` cells) is deprecated for new work — both still produce valid
silicon, but `.rkt` is the format the viz tool, tape-out flow, and
the SoC integration path now standardize on.

If you're tempted to write a `.mag` file or a layout-generating TCL
script for a new cell, **stop and read this doc first**. There's
almost always a better answer here.

---

## The mental model

```
┌────────────────────────────────────────────────────────────┐
│ Primitive (PDK-owned, machine-generated)                   │
│   nfet_hv_W1p2_L1p0_core.rkt                               │
│   - Silicon-truth mask layers (diff, poly, mcon, met1, …)  │
│   - Carries a (meta (generator …) (params …)) block        │
│   - Authored by calling a Python generator that shells out │
│     to Magic + the PDK's own draw proc                     │
│   - DO NOT hand-edit the geometry                          │
└────────────────────────────────────────────────────────────┘
           │  imported by
           ▼
┌────────────────────────────────────────────────────────────┐
│ Block (agent- or human-authored)                           │
│   blc_comparator_v10.rkt                                   │
│   - (import "primitives/<name>.rkt") at the top            │
│   - (sref (cell <prim>) (origin …)) for each instance      │
│   - (rect …) / (poly …) for parent-paint (wells, taps,     │
│     power rails, signal routing)                           │
│   - YOU CAN AND SHOULD edit this directly                  │
└────────────────────────────────────────────────────────────┘
           │  exported by
           ▼
       Rkt.ToGds → GDS for tape-out
```

Two layers, one format. The split is enforced by the `(meta …)` block:
its presence marks a cell as PDK-owned; consumers (viz editor,
tape-out) honor that.

---

## Authoring a primitive

You don't author primitives directly. You **call a generator** that
mints one. Today's generators live in `src/rekolektion/primitives/sky130/`:

```python
from rekolektion.primitives.sky130 import gen_nfet_hv, gen_pfet_hv

nfet = gen_nfet_hv(w_um=1.2, l_um=1.0, guard=False)
pfet = gen_pfet_hv(w_um=2.4, l_um=1.0, guard=False)
# Returns the cell name as a string, e.g. "nfet_hv_W1p2_L1p0_core".
# The .rkt is written to cell_designs/primitives/<name>.rkt.
# Identical params on a later call are a cache hit (no Magic spawn).
```

**Available generators (today):**

| Function | What it makes | Notes |
| -------- | ------------- | ----- |
| `gen_nfet_hv(w_um, l_um, nf=1, m=1, guard=False, topc=True, botc=True)` | sky130 5 V HV nfet (`sky130_fd_pr__nfet_g5v0d10v5`) | see below |
| `gen_pfet_hv(w_um, l_um, nf=1, m=1, guard=False, topc=True, botc=True)` | sky130 5 V HV pfet (`sky130_fd_pr__pfet_g5v0d10v5`) | bulk port labeled `B` at primitive level (FET generator v4, 2026-05-29) — extracted .subckt is `D G S B`, parent binds body explicitly by position |
| `gen_nfet_01v8(w_um, l_um, nf=1, m=1, guard=False, topc=True, botc=True)` | sky130 1.8 V LV nfet (`sky130_fd_pr__nfet_01v8`) | same shape as HV; used for VCCD1 / VCCD2 analog (Track 37 BG, OTA, etc.) |
| `gen_pfet_01v8(w_um, l_um, nf=1, m=1, guard=False, topc=True, botc=True)` | sky130 1.8 V LV pfet (`sky130_fd_pr__pfet_01v8`) | bulk port labeled `B` at primitive level (v4) — same as `gen_pfet_hv` |
| `gen_pnp_05v5(kind, nx=1, ny=1)` | 5.5 V substrate PNP — `kind="small"` → W=0.68×L=0.68 µm, `kind="large"` → W=3.40×L=3.40 µm (≈25× area ratio) | Fixed-geometry device (no `w`/`l` knob); the variant IS the size. Canonical sub-bandgap pair |
| `gen_res_xhigh_po(width_um, l_um, m=1, guard=False, snake=0)` | xpoly high-sheet poly resistor (~2 kΩ/□) | `width_um` must be one of `0.35`, `0.69`, `1.41`, `2.85`, `5.73` µm (PDK-fixed widths); `l_um` is free |
| `gen_cap_mim_m3(w_um, l_um, stack=1)` | MIM capacitor between met3 + capm (`stack=1`) or capm + capm2 (`stack=2`) | W and L each must be 2.0 ≤ x ≤ 30.0 µm. Lives above met3 — area underneath on li1/met1/met2 is free |

**Two topology axes that matter for routing:**

- `guard`: `True` adds a per-cell substrate guard ring (a standalone FET). Default `False` gives a `_core` variant designed to abut or sit in a tub.
- `topc` / `botc`: which side(s) carry a gate contact. **Default is `True/True`** (gate contacts on both top and bottom), which is right for hand-routed analog where the gate may be tapped from either direction.

| Gate-contact choice | Suffix | When to use |
| ------------------- | ------ | ----------- |
| `topc=True, botc=True` (default) | none | Gate may be tapped from any side; hand-routed analog |
| `topc=True, botc=False` | `_topgate` | FET sits *above* a rail. S/D li1 has clear vertical egress *down* to the rail — required for `pin_to_rail` to produce a DRC-clean route |
| `topc=False, botc=True` | `_botgate` | FET sits *below* a rail. S/D li1 has clear egress *up* to the rail |

**For std-cell-row blocks** (nfets over VSS, pfets under VDD):

```python
nfet = gen_nfet_hv(w_um=1.0, l_um=0.5, botc=False)   # _topgate
pfet = gen_pfet_hv(w_um=1.0, l_um=0.5, topc=False)   # _botgate
```

Why it matters: with default `botc=True`, the bottom gate contact is at `x=(-250, 250)` and the S/D li1 strips are at `x=(310, 480)` / `x=(-480, -310)`. Spacing between them is 60 nm — fine when the S/D li1 stays inside the FET footprint (different y), but if `pin_to_rail` extends S/D li1 *past* the FET's bottom edge, it now lives at the same y as the gate stub and the 60 nm gap violates `li.3`. Using `botc=False` removes the conflicting stub entirely.

> **The same collision hits body taps placed south of a default
> (both-contact) PMOS — and it's silent at DRC.** `place_taps_around`
> with `sides=("bottom",)` lands the tap's li1 strap right under the
> primitive's bottom gate li1 stamps. Concrete example for the
> `pfet_hv_W25p0_L0p5_nf10` default variant: bottom gate li1 stamps at
> y=-12905..-12735; default tap-band li1 strap at y≈-13175..-12845 →
> **60 nm overlap in y** at the same plane. The two polygons merge
> on li1; the extracted gate net becomes the body net.
>
> DRC waivers around primitive footprints typically hide the
> underlying `li.3` violation, so the only signal is LVS — and the
> bug masquerades as a "gate↔body merge through autonamed parent
> metal" because the extractor names the merged polygon by its
> dominant layer / corner. Time burned chasing the wrong cause: real.
>
> **Rule:** for any PMOS that will receive a south-side body tap,
> mint it with `botc=False` (`_topgate`); for north-side taps,
> `topc=False` (`_botgate`). Phase A srcmux escaped this trap because
> body=S=VDDA1 there — the spurious merge produced the right net by
> coincidence. Cells where body ≠ source (Phase B1 `cim_reram_bl_swap_mux`,
> any pass-gate MUX, any cascode where body needs to track VDD)
> have no such coincidence and will fail LVS. See **Hard Rule #20**.

> **`_core` means "designed to abut or live in a parent tub."** It is
> *not* a "smaller" or "lighter" primitive — it's a primitive that has
> the per-cell guard ring stripped, with the assumption that you'll
> either abut multiple `_core` cells (their wells merge) or place
> them inside one big parent-painted nwell/psub region. If you place
> `_core` cells with a small gap between them, you will hit
> `nwell.2a` and a cascade of related DRC violations. Read **Placing
> `_core` primitives** in the next section before you write any
> SRefs.

> **Prefer `nf=1` for current mirrors / matched-FET cells; reserve
> `nf>1` for cases where finger-matching is the design intent.**
> Multi-finger FETs (`nf=2`, D-S-D pattern) have two drain
> polycontacts (D0, D2) on separate li1 columns inside the
> primitive — the parent must short them externally on met2 with a
> via1 jumper, *and* Magic's hierarchical extraction is flaky about
> merging the two fingers' GATE polycontacts at the parent level
> (sometimes autonames one of them with the primitive cell name,
> failing parallel-device merging in netgen). The result: an LVS
> mismatch where the layout has more device instances than the
> schematic, even with the geometry correct. For a 3-PMOS current
> mirror at W/L = 20 µm / 2 µm, calling `gen_pfet_01v8(w_um=20, l_um=2, nf=1)`
> gives an electrically identical FET as `nf=2, w_um=10` but cleaner
> extraction and no external D-D jumper. If you specifically need
> multi-finger for matching (common-centroid, dummy fingers),
> accept the extraction complexity and label EACH finger's gate/
> drain explicitly at the parent so the labels propagate.

> **Fixed-geometry primitives are multi-cell `.rkt` files.** PNPs,
> VPP caps, varactors, inductors — anything the PDK ships as a
> pre-laid-out `.mag` under `libs.ref/sky130_fd_pr/mag/` and draws
> via `getcell` — produce a `.rkt` with TWO cells: the generator's
> wrapper cell + the PDK cell as a child SRef at origin (0, 0).
> Placement helpers (`inspect_primitive`, `place_row`, `place_tub`,
> `pin_patch`) handle this transparently — they read the `(top …)`
> declaration and union all geometry. You don't need to flatten
> anything by hand. (If a future generator places a child SRef at
> non-(0, 0) origin, `read_primitive` will raise a clear error
> until the bbox helper learns hierarchical translation.)

**Adding a new generator?** Follow the pattern in `fet.py`:

1. Call the PDK's defaults proc (`sky130::<dev>_defaults`)
2. Merge your caller params on top
3. Pass the merged dict to the PDK's draw proc (`sky130::<dev>_draw`)
4. Let the runner shell out and pipe the GDS back through `_gds_to_rkt`
5. Attach a `(meta (generator "sky130/<name>") (params …))` block

**DO NOT** reimplement device geometry in Python — the PDK's TCL
procs are the source of truth for mask-layer placement, contact
arrays, implant margins, and so on. Forking that code commits us to
maintaining DRC compatibility forever.

---

## Authoring a block

A block is a `.rkt` file under `cell_designs/<group>/<name>.rkt`.
Before you write a single SRef, decide **how** you're going to
place primitives, because the wrong choice eats hours in DRC
debugging. The next section covers that; then read the three
authoring options.

### Placing `_core` primitives — abut or tub, never gap

This is the single biggest source of preventable DRC violations.
**Read it.**

Every `gen_*` call with `guard=False` returns a `_core` primitive
that carries its own per-cell `nwell` (for pfets) or implicit `psub`
(for nfets), sized to just contain the FET's diffusion plus implant
margins. The well is a rectangle baked into the primitive's `.rkt`.

When you SRef two pfet `_core` primitives near each other, their
two nwell rectangles end up near each other in the parent. SKY130's
**nwell.2a** rule says: two nwells must be either

- **abutting** (gap = 0; the wells become one polygon and the rule
  doesn't apply), **OR**
- **far apart** (gap ≥ **1.27 µm** = 1270 DBU).

Anything in between — 1 nm, 100 nm, 200 nm, 1.0 µm of gap —
**is a DRC violation**. There is no legal "small gap" between
nwells. Same trap exists for `hvi` (high-voltage implant), `nsdm`,
`psdm`, and well taps — they all have an abut-or-far rule.

#### Use the helpers — don't compute origins by hand

There are two helpers in `rekolektion.layout` that encode the two
valid patterns and refuse to produce the invalid one. **Use them
first; only drop to hand-computed origins if a helper genuinely
can't express what you need** (and tell us — that's a helper gap).

```python
from rekolektion.layout import place_row, place_tub
```

**`place_row(primitive_names, axis='x'|'y', origin=(0,0))`** —
Pattern A. Returns a list of `rkt.SRef` with origins computed so
adjacent cells abut at their bbox edges. Same-well-type only;
mixing nfet + pfet raises `ValueError`. `place_row` only advances
along `axis`; the orthogonal coordinate is passed through from
`origin` unchanged.

```python
nfet = gen_nfet_hv(w_um=1.2, l_um=1.0)
row = place_row([nfet] * 4)        # 4 identical nfets, wells merge
# row[0].origin → (975, 0); row[1].origin → (2925, 0); …
```

> **Pfet row + `place_taps_around` is a trap.** `place_row` paints
> nwell only as wide as the abutted FETs themselves. Nwell taps from
> `place_taps_around(..., 'nwell', sides=('top', 'bottom'))` sit
> OUTSIDE that band (above/below the FET row), so the tap's n-tap
> rectangle has no nwell over it and DRC fails `diff/tap.10` (N-well
> overlap of N-tap < 0.18 µm). For pfet rows that need taps, use
> `place_tub(..., margin_um >= 1.0)` instead — the parent-painted
> nwell extends past the FET row and covers the tap bands. `place_row`
> + tap-band is fine for **nfet** rows (psub is the default substrate;
> no tap-enclosure rule).

> **Abutting two FETs does NOT auto-merge their S/D li1.** `place_row`
> abuts cells so wells/implants merge, but each FET primitive's S/D
> li1 strip stops ~195 nm *inside* its own cell edge — when M_IN1
> sits at the left and M_IN2 sits at the right with pitch=width,
> M_IN1.S's li1 and M_IN2.D's li1 are ~390 nm apart in the parent.
> Magic's extractor sees two separate nets, and LVS reports a
> mismatched `Number of nets` with the unmerged terminal showing up
> on an autonamed net like `<primitive>_0/D`. **Fix:** paint an
> explicit li1 bridge between the two pin coords with
> `place_wire(parent_coord(s1, "S"), parent_coord(s2, "D"), layer="li1")`,
> then label the bridge's midpoint with the shared net name. The
> bridge overlaps both pin li1s and fuses them into one extracted
> net. Rule of thumb: **abutment merges wells; it never merges
> metal.** Any shared-S/D topology (cascodes, folded pairs, series
> stacks) needs an explicit li1 or met1 bridge.

**Stacking multiple rows vertically.** A primitive's bbox is
**centered around the cell origin** (e.g. y_min ≈ -1060,
y_max ≈ +1060 for a typical HV FET). So the next row's y-origin
should be computed from the previous row's `bbox.y_max + clearance`,
**NOT** from `bbox.height + clearance`:

```python
nfet = gen_nfet_hv(w_um=1.2, l_um=1.0)
pfet = gen_pfet_hv(w_um=1.2, l_um=1.0)

nfet_info = inspect_primitive(nfet)
# Bottom row: nfets at y = 0  (their bboxes extend to nfet_info.bbox[3])
nfet_row = place_row([nfet] * 4, origin=(0, 0))

# Top row: pfets sit above with 6 µm clearance.
# y_top = nfet bbox y_max + 6 µm (NOT nfet_info.height + 6 µm).
pfet_y = nfet_info.bbox[3] + 6000
pfet_row = place_row([pfet] * 4, origin=(0, pfet_y))
```

If you find yourself reaching for `bbox.height` to position a
neighboring row, stop and use `bbox[3]` (y_max) instead. The center-
on-origin convention is what `gen_*_hv` produces today, and the
helper math assumes it.

**`place_tub(primitives, well_layer=None, extra_layers=None, margin_um=0.4)`** —
Pattern B. Paints a parent `nwell` (or `pwell`) over the union of
all primitives + margin, auto-adds `hvi` when any primitive is HV,
and returns a `TubResult` with `.well_rects + .srefs` (or `.elements`
for "everything in one list"). Same-well-type only.

**`place_taps_around(inner_bbox, well_type, sides=('top','bottom'))`** —
Substrate / well-tap helper. Paints DRC-clean tap bands (tap +
implant + periodic licon contacts + li1 strap) around an active
region. **`_core` primitives don't include taps** — without them
your block fails the `tap.5` (every well needs a substrate
connection) extraction step and is latch-up vulnerable in silicon.

```python
nfet = gen_nfet_hv(w_um=1.2, l_um=1.0)
row = place_row([nfet] * 4)

# Union bbox of the placed row, in parent coords.
info = inspect_primitive(nfet)
xs1 = min(s.origin[0] + info.bbox[0] for s in row)
ys1 = min(s.origin[1] + info.bbox[1] for s in row)
xs2 = max(s.origin[0] + info.bbox[2] for s in row)
ys2 = max(s.origin[1] + info.bbox[3] for s in row)

taps = place_taps_around((xs1, ys1, xs2, ys2), 'pwell')
# Drop straight into the cell:
elements = [*row, *taps.elements]
```

The helper warns when the inner bbox's longest extent exceeds
~14.85 µm — above that the surround-only approach may miss the
periodic-tap (latch-up) rule.

**The two fixes (preferred order):**

1. **Perimeter tap band at the *parent* cell** — preferred for hand-
   laid analog. The block stays matching-clean; latch-up gets closed
   by a guard-ring-style tap band the parent paints around the whole
   active analog region (the band also serves any neighbouring cells
   in that parent, so it's reused, not wasted). This is how real
   analog ICs handle latch-up: blocks worry about their own well
   continuity (the surround-only taps from `place_taps_around` are
   enough for that), and the parent worries about chip-level
   periodic taps via a perimeter band that wraps multiple blocks.
2. **Interspersed tap row inside the block** — fallback for cases
   where the block has no enclosing parent (a top cell, or a macro
   that ships standalone). Costs cell area (~0.85 µm of extra
   height/width per row) but doesn't impair matching — an nwell tap
   row sits in the nwell band, doesn't touch any channel, and
   common-centroid layouts routinely include them.

**Do NOT** "defer with a checklist" — there is no automated catch
for missed periodic taps. P&R tools treat analog macros as
black-box LEF abstracts and won't reach inside to add tap rows.
Full-chip DRC eventually flags it, but by then re-fixing forces
costly re-routing of whatever sits next to your block. Pick one of
the two fixes above before sign-off; document the parent-level
fix in the project's track plan or floorplan doc so it lands at
integration time.

**Where tap bands go relative to the well:**

| Surrounded primitives | `well_type` | Where the tap band sits |
| --------------------- | ----------- | ----------------------- |
| nfets in psub (no tub) | `'pwell'`  | Anywhere outside the FETs — psub is the default substrate |
| pfets in a `place_tub` nwell | `'nwell'` | **Inside the nwell tub**. The nwell is what the taps contact; if they sit outside the tub, they have no well to contact |

When in doubt, place tap bands such that their `tap` rectangle is
**inside** the well rectangle they're tapping. The nwell tub must
extend past the tap bands' implant rectangle — not just past the
FET bbox.

**Sizing the tub margin when taps go inside.** `place_tub`'s default
`margin_um=0.4` extends the tub 0.4 µm past the union FET bbox.
That's enough when the tub holds primitives ALONE, but `place_taps_around`
puts its tap band at `FET_bbox + clearance_um (0.3) + tap_width (0.42) ≈ 0.72 µm`
outside the FET bbox — *outside* the default 0.4 µm tub. DRC fails
on `nwell.1` / `diff/tap.10` because the n-tap isn't enclosed by
nwell.

**Use `margin_um ≥ 1.0`** (or roughly `clearance + tap_width + nwell.tap_encl ≈ 1.05 µm`)
whenever you'll add tap bands inside the same tub:

```python
# Pfet tub with room for nwell taps inside:
tub = place_tub(
    [(pfet, (0, 0)), (pfet, (3000, 0))],
    margin_um=1.2,                        # large enough for taps
)
# Now the nwell tap bands fit comfortably inside.
nwell_taps = place_taps_around(pfet_bbox, "nwell", sides=("top", "bottom"))
```

`place_tub`'s default stays small because tubs without inside-taps
(typical for "just a small parameterized pmos cluster") shouldn't
pay area cost they don't need. If you find yourself getting nwell-
enclosure DRC errors after adding taps, bump the tub margin.

**Tying tap straps to VSS / VDD rails — use `place_rail`.** The tap
helper stops at the li1 strap because the via stack up to met1
depends on rail orientation, layer, and surrounding routing. The
companion `place_rail(bbox, label, stitch_li1_straps=…)` paints the
met1 rail, labels it, and auto-fills the strap/rail overlap with an
mcon contact array — closing the loop in one call.

Most blocks have **two rails (VSS at the bottom, VDD at the top)
each absorbing one tap strap**. Use `tap.li1_straps_by_side` to
hand each rail only the straps it overlaps:

```python
from rekolektion.layout import place_taps_around, place_rail

tap = place_taps_around(
    active_bbox, "pwell", sides=("top", "bottom"),
)
straps = tap.li1_straps_by_side       # {"top": [...], "bottom": [...]}

# Each rail absorbs only its side's strap. The rail's bbox must
# overlap that strap; non-overlapping straps emit a "doesn't overlap
# rail" warning and are skipped, so always split by side here.
vss_rail = place_rail(
    (block_x_min, vss_y_min, block_x_max, vss_y_max),
    label="VSS",
    stitch_li1_straps=straps["bottom"],
)
vdd_rail = place_rail(
    (block_x_min, vdd_y_min, block_x_max, vdd_y_max),
    label="VDD",
    stitch_li1_straps=straps["top"],
)

cell.elements.extend([*tap.elements, *vss_rail, *vdd_rail])
```

If a single rail spans the whole block and absorbs every strap, use
`tap.li1_straps` directly. For the (rare) case where one rail is
intentionally connected to a strap that doesn't overlap it, paint
the bridging met1 wire yourself and add the strap to the rail call
that *does* overlap it.

> **Extend the rail 30 nm past the strap on the long axis.** The
> stitch helper insets its mcons by `MET1_ENCLOSURE_OF_MCON = 0.03`
> on every side of the rail/strap overlap — that satisfies met1.5's
> *narrow* enclosure rule (≥30 nm one side) but not the *wide* one
> (≥60 nm on the perpendicular axis). If the rail bbox exactly
> matches the strap bbox, you get 30/30 enclosure on both axes for
> mcons at the strap ends and DRC fails met1.5. The fix: pass
> `rail_bbox=(xs1, strap_y_min - 30, xs2, strap_y_max + 30)` for a
> horizontal strap (extend in y), or the orthogonal version for a
> vertical strap. The 30 nm extension brings the wide-axis enclosure
> to 60 nm and passes the asymmetric rule.

```python
pfet = gen_pfet_hv(w_um=2.0, l_um=2.0)
tub = place_tub([
    (pfet, (0,    5000)),
    (pfet, (3500, 5000)),    # non-uniform spacing for matching
    (pfet, (8000, 5000)),
])
# Drop into a cell:
cell = rkt.Cell(name="my_block", elements=[*row, *tub.elements])
```

#### The two patterns (conceptual background)

Read this so you know what the helpers are doing under the hood,
and so you can choose between them.

**Pattern A — std-cell-row (abutting wells).** Same-well-type cells
in a row with pitch = primitive width. Wells merge into a
continuous band, `nwell.2a` satisfied by construction. How every
digital standard cell library is laid out. `place_row` does this.

```
parent paint: ───────────────────────────────────────
              │ pfet_core │ pfet_core │ pfet_core │   <- wells abut, merge
              └──────────┴────────────┴───────────┘
y = 0:        │ nfet_core │ nfet_core │ nfet_core │   <- separate row, psub
              └──────────┴────────────┴───────────┘
```

**Pattern B — analog tub (parent-painted shared well).** Paint one
big `nwell` + `hvi` over the entire pfet region at the parent
level. Drop `_core` pfets inside at any spacing — their per-cell
nwells are then geometrically *inside* the parent's nwell, no
inter-cell boundary exists, and the rule doesn't trigger. Use when
matching / symmetry demands non-uniform spacing (diff pairs,
current mirrors). `place_tub` does this.

#### NOT a valid pattern

**Arbitrary gaps between `_core` cells.** Picking `GAP_CELL = 200`
nm to "give the cells some breathing room" — this is the
no-man's-land between abutment and the 1.27 µm minimum. It will
fail `nwell.2a` (and several other implant/well rules) on every
adjacent same-well-type pair.

If you find yourself reaching for a "spacing" constant between
`_core` cells, **stop**. Use `place_row` (abut) or `place_tub`
(parent well). There is no third option.

#### Diagnosing a violation

Open the block in `viz app`, isolate the `nwell` layer (or `psdm`,
`nsdm`, `hvi` — same rule applies to those), and look for any
non-zero gap shorter than ~1.3 µm between same-color rectangles. If
you see one, the placement is wrong — fix the pitch or paint a tub.

`dotnet run -- viz-render` with only well layers enabled gives a
quick CI-friendly check.

### Three authoring options

#### Option A — programmatic (Python)

Best when the block is arrayed, parameterized, or has many instances.
Use `rekolektion.io.rkt` for the document structure and
`rekolektion.layout` for placement.

```python
from pathlib import Path
from rekolektion.io import rkt
from rekolektion.layout import place_row, place_tub
from rekolektion.primitives.sky130 import gen_nfet_hv, gen_pfet_hv

# Mint the primitives. Cache-aware — same params skip Magic.
nfet = gen_nfet_hv(w_um=1.2, l_um=1.0)
pfet = gen_pfet_hv(w_um=2.0, l_um=2.0)

# Pattern A: a row of four nfets, abutting (psub merges).
nfet_row = place_row([nfet] * 4, origin=(0, 0))

# Pattern B: three pfets in a parent-painted nwell tub, freely
# spaced (matching-style placement). place_tub auto-adds `hvi`
# for HV devices.
tub = place_tub(
    [(pfet, (0, 5000)),
     (pfet, (3500, 5000)),
     (pfet, (8000, 5000))],
)

doc = rkt.Document(
    imports=[
        # Import paths are RELATIVE TO THE BLOCK FILE'S LOCATION.
        # A block at cell_designs/my_group/my_block.rkt reaches the
        # primitives at cell_designs/primitives/ via "../primitives/".
        rkt.Import(path=f"../primitives/{nfet}.rkt"),
        rkt.Import(path=f"../primitives/{pfet}.rkt"),
    ],
    cells=[
        rkt.Cell(
            name="my_block",
            elements=[
                *nfet_row,
                *tub.elements,           # well paint + pfet srefs
                # Parent-paint signal routing on top (labels too —
                # see "Naming nets" below):
                rkt.Rect(layer=rkt.named("sky130", "met1"),
                         x1=0, y1=-1000, x2=7800, y2=-200),
                rkt.Label(layer=rkt.named("sky130", "met1_label"),
                          text="VSS", origin=(3900, -600)),
            ],
        ),
    ],
    top_cell="my_block",
)

Path("cell_designs/my_group/my_block.rkt").write_text(rkt.write(doc))
```

Coordinates are in DBU (1 nm by default — see `(units (dbu_nm 1))`).
**Notice that no SRef origin is hand-computed** — `place_row` and
`place_tub` derive them from each primitive's bbox so wells abut /
the tub covers correctly. If you find yourself writing
`origin=(<magic number>, ...)` for an SRef, ask whether a helper
should be doing it.

**Import-path rule of thumb:**

| Block lives at                            | Import path to a primitive  |
| ----------------------------------------- | --------------------------- |
| `cell_designs/<group>/<block>.rkt`        | `../primitives/<name>.rkt`  |
| `cell_designs/<block>.rkt` (rare)         | `primitives/<name>.rkt`     |
| `demo_output/<block>.rkt` (scratch space) | `primitives/<name>.rkt`     |

When in doubt, count directories from your block file up to the
`cell_designs/` root — that's how many `../` you need.

#### Option B — hand-authored `.rkt`

Best for one-off blocks where Python adds no value. The schema is in
`docs/io/rkt.md`. A minimal example:

```scheme
; Two HV FETs bridged by a met1 strap.
; Saved as cell_designs/my_group/my_block.rkt — note the "../primitives/"
; prefix: imports are relative to THIS file's location, and the
; primitives sit one directory up under cell_designs/primitives/.
(layout (version 1)
  (pdk sky130)
  (units (dbu_nm 1) (uu_um 1))
  (import "../primitives/nfet_hv_W1p2_L1p0_core.rkt")
  (import "../primitives/pfet_hv_W1p2_L1p0_core.rkt")
  (top my_block)
  (cell my_block
    (sref (cell nfet_hv_W1p2_L1p0_core) (origin 0 0))
    (sref (cell pfet_hv_W1p2_L1p0_core) (origin 4000 0))
    (rect (layer sky130:met1) 1055 -150 2945 150)))
```

But the primitives have to **exist on disk first**. Call the Python
generators in a notebook or REPL before hand-authoring the imports —
the file the import points to has to be there for viz to render
properly.

#### Option C — hybrid

Python emits the skeleton (imports + sref scaffolding), then a human
or agent tweaks placement, adds parent paint, fills in nets. The
`.rkt` round-trips through both the Python writer and the F# editor,
so it doesn't matter which side authored which lines.

---

## Placement review — STOP and show the user before routing

After cells are placed and tap/rail geometry is in — but **before any
signal routing** (Phase 1 `pin_to_rail` and onward) — pause and
present the geometry to the user in viz for approval or redirection.

This is a **hard gate**.  Placement is the architectural decision
that locks every downstream routing channel: aspect ratio, which
inverters align vertically, which pins are cross-row vs same-row,
where the inter-row routing channel sits, what nets need to jog
around what.  Once you start wiring, every redirect costs N turns of
unwinding.  Catching a bad placement before any wire is painted is
the cheapest loop in the entire workflow.

The agent surfaces, the user decides whether to keep going:

1. **Open in viz**: `mcp__rekolektion-viz__rekolektion_viz_open` (or
   `dotnet run -- app cell_designs/<group>/<block>.rkt`) so the user
   can see the silicon-truth view. **Pass the `.rkt` path** — never
   `to-gds` your own block and open the GDS (Hard Rule #17). **State
   the absolute path of the file you just opened in the user-facing
   message** (Hard Rule #18) — `viz_open` returns silently and the
   user cannot tell from the chat which file is now active. Format:
   `"Opened /abs/path/to/<block>.rkt in viz."` — single sentence,
   path verbatim.
2. **Describe what's there in text**, since the user may scan the
   summary before opening viz:
   - Block dimensions (W × H).
   - Per-row cell list with center x positions.
   - Which inverter / diff-pair partners align vertically and which
     don't, and why (e.g. "P2 is offset 395 nm right of N2 because
     P2's nf=2 width doesn't match N2's nf=1 width").
   - Aspect-ratio quirks (e.g. "block is 25 µm tall because W=10
     L=0.5 cells dominate the row height").
   - Which signal nets will be cross-row (need met2 vertical) vs
     same-row (likely met1 horizontal or li1 abut).
3. **Ask one plain-language question**: "placement OK or want
   changes?"  Per `feedback_state_question_first` — lead with the
   question, not an option matrix.

Routing **does not start** until the user signs off.  This is true
for *every* new block, every layout-from-scratch.  Editing an
existing block to fix a known bug is exempt — that's a maintenance
edit, not a fresh authoring pass.

### Why not just iterate via DRC?

DRC catches manufacturability, not topology.  A block can be
DRC-clean with the inverter pair mis-aligned, the gates on different
rows, or the pulldown FET 10 µm from where it needs to be — DRC
won't complain, but routing then has to bend around the
misplacement, you'll burn budget on jogs and bridges, and the user
will redirect you anyway after seeing the result.  Skip the bend by
asking up front.

### What "approval" covers and what it doesn't

- **Approval covers**: cell positions, row arrangement, rail
  locations, tap band placement, overall block dimensions, port-pin
  side decisions.  These are frozen until the user explicitly
  changes them.
- **Approval does NOT cover**: routing-layer choices, individual
  wire paths, jog corners, via stack details.  Those are agent-level
  decisions that get DRC/LVS-gated downstream.

If a routing-phase DRC failure forces a placement change — e.g. a
4-pin net genuinely cannot route in the available channels — that
itself is a decision point: stop, propose the placement edit, get
re-approval.  Don't silently re-place.

---

## SRef transforms — `reflect` and `rot`

When you SRef a primitive (or sub-block) with `reflect=True` and/or
`rot=...`, the cell-local pin/strap coordinates DON'T trivially
become parent coordinates — they go through a 2D affine transform.
**Getting this wrong is a silent layout bug:** DRC passes, viz
renders the FET in the visually-right place, and netgen sometimes
even reports "netlists match uniquely" because transistor S/D are
symmetric — but your parent labels and parent-painted wires land on
the wrong pins.  By the time chip-level LVS fails, the wrong-pin
routing has propagated into the parent integration.

### `reflect=True` means **Y-flip**, not X-flip

Per GDS convention and rekolektion's F# reader/writer
(`Mag/Transform.fs`), `(reflect true)` mirrors the cell across the
X-axis — i.e., **negates Y** while keeping X.  This is the
mirror-about-X-axis convention every other GDS-aware tool uses.

If your mental model is "reflect swaps S and D in x," that's the
opposite of what the format does — you'll silently wire labels to
the wrong pins.

### Composed transform matrix (per `Mag/Transform.fs`)

| `reflect` | `rot`  | Linear part `[[a, b], [c, d]]`          | Effective op            |
| --------- | ------ | --------------------------------------- | ----------------------- |
| `False`   | `0°`   | `[[1, 0], [0, 1]]`                      | identity                |
| `False`   | `180°` | `[[-1, 0], [0, -1]]`                    | full 180° rotation (X+Y flip) |
| `True`    | `0°`   | `[[1, 0], [0, -1]]`                     | **Y-flip only**         |
| `True`    | `180°` | `[[-1, 0], [0, 1]]`                     | **X-flip only**         |

So:

- For S/D pin x-swap with gate y position preserved (the cascode
  layout's "I want NMOS_CASC.S to land on the FAR side so the
  bridge to NMOS_BOT.D is a clean vertical"): use **`reflect=True`
  AND `rot=180°` together**.  This is the operation that's commonly
  meant by "mirror the cell horizontally."
- For S/D pin x-swap with gate moving to the bottom of the cell:
  use `rot=180.0` alone.
- For Y-flip (cell topology inverted vertically; gate moves from
  top to bottom but x-positions unchanged): use `reflect=True`
  alone.
- For identity: leave both at defaults.

### Computing parent pin coordinates correctly

Given a cell-local pin at `(px, py)`, parent coords are:

```python
import math
def parent_pin(sref_origin, px, py, *, reflect=False, rot=0.0):
    theta = math.radians(rot)
    c, s = math.cos(theta), math.sin(theta)
    if reflect:
        # M = [[cos, sin], [sin, -cos]]
        nx = round(c*px + s*py)
        ny = round(s*px - c*py)
    else:
        # M = [[cos, -sin], [sin, cos]]
        nx = round(c*px - s*py)
        ny = round(s*px + c*py)
    return (sref_origin[0] + nx, sref_origin[1] + ny)
```

The `inspect_primitive(...)` helper returns cell-local pin coords;
THIS function maps them to parent for label placement and routing
endpoints.  **Don't write a one-liner that assumes flip-X for
reflect — use the matrix.**

### How the bug manifests if you get it wrong

You point parent labels and wires at coordinates computed under
the wrong transform.  The labels end up:

- On the wrong primitive pin (because Magic's hierarchical extract
  resolves them via the SRef geometry, which IS correct), OR
- In empty space (no underlying polygon, so the label becomes a
  floating port in the .ext, marked as a "disconnected node" in
  netgen LVS).

In netgen's comparison you see two patterns simultaneously:

1. **"Cell X disconnected node: PORT_NAME"** — the parent label
   for that port hovers over empty space at the assumed-but-wrong
   pin coord.
2. **"Netlists match uniquely with port errors"** — netgen still
   finds an isomorphism because BSIM4 transistor S/D are symmetric,
   so it silently swaps S and D in the layout to match the schematic.
   You read this as "topology is right" and ship the wrong silicon.

The bug compounds when one of the affected pins is an internal
node — netgen might list the duplicated internal node as a port
twice (once for the real connection, once for the floating label).

**Fix:** correct the transform interpretation, recompute every
parent pin coord via the composed matrix, move labels and wire
endpoints, re-verify.  Don't accept "netlists match uniquely with
port errors" as a green light when reflect is in play — it's
exactly the signal that S/D swap may be masking a real bug.

### Sanity check after any transform change

After writing or modifying any SRef with non-default `reflect` or
`rot`:

1. Compute the new parent pin coords via the matrix above.
2. Open the block in viz and confirm each pin label sits ON the
   primitive's actual strap (not in empty space, not on a
   neighboring strap).
3. Run `verify_lvs`.  If you see "disconnected node" for any port
   on the transformed cell, OR "netlists match uniquely with port
   errors" combined with duplicate port entries, the transform
   handling is wrong — DON'T paper over it with `port_aliases` or
   parent-paint patches.  Fix the transform.

For a worked example of how the misinterpretation cascades through
LVS, see "Common LVS failure modes" under *Step 3 — LVS*.

---

## Routing signals — direction conventions and helpers

Once primitives are placed, tap/rail geometry is in, **and the user
has signed off on placement** (see *Placement review* above), you
wire the signal nets that aren't rails. The two traps here are
picking the wrong layer (everything on met1 → instant hairball +
DRC failures where wires cross) and skipping the parent met1 patch
that every cell pin needs before via1 can land on it.

### Preferred routing direction

SKY130 follows the standard CMOS HVH-VHV alternation. Each metal
layer has a *preferred* axis along which DRC width/spacing is
tightest. Routing against the preferred axis is legal but costs
area and routing resources.

| Layer  | Preferred axis | Routing pitch | Typical use |
| ------ | -------------- | ------------- | ----------- |
| `li1`  | vertical / free | 0.46 µm      | Intra-cell only |
| `met1` | **horizontal** | 0.34 µm       | Std-cell rails + pin stubs + supply rails |
| `met2` | **vertical**   | 0.46 µm       | Cross-row signal routes |
| `met3` | **horizontal** | 0.68 µm       | |
| `met4` | **vertical**   | 0.92 µm       | |
| `met5` | **horizontal** | 1.60 µm       | Global power straps |

The mapping is encoded in `tech/sky130.py` as `ROUTING_DIRECTION`
(an `Axis` enum lookup) and `ROUTING_PITCH_UM`. Helpers consult it;
agents should too when picking layers for a route.

**Rule of thumb for analog blocks:** met1 horizontal for short pin
stubs and supply rails, met2 vertical for any cross-row signal
(connecting an nfet's drain to a pfet's gate, etc.). Use met3+
only when met2 routing channels are full.

### HARD RULE — offset perpendicular met1 wires off FET S/D pins

When you need to run a met1 wire vertically off an nfet/pfet `01v8`
`_core` S or D pin, **center the wire on `pin_x ± 200 nm` (OUTWARD
into the inter-cell channel), not on the pin x itself.** D pins
offset left, S pins offset right (for unrotated cells; flip per
sref rotation).

Why: the `_core` primitive paints a 4-strip met1 ring inside the
cell — two vertical S/D frames at the pin edges (S/D net) plus two
horizontal top/bottom strips at `y ≈ ±cell_top` (**GATE net**, with
the gate contact stack under them). The vertical frames extend
to cell-local ±5260/-5030 (for W=10) and the horizontal gate strips
start at cell-local ±4980 — only ~50 nm inside the S/D frames. A
wire centered on the pin lands ≤5 nm shy of the gate strip → trips
`met1.2` (140 nm min spacing) AND electrically shorts S/D to GATE.

**How to apply, every time:**

1. **Do not** drop your own mcon at the pin — the primitive already
   paints multiple mcons there for its own contact stack. An added
   mcon at the same x lands ~10 nm from the primitive's mcons →
   `mcon.2` (190 nm spacing) violations.
2. Place the vertical wire centered at `pin_x ± 200 nm` (OUTWARD
   from cell center). 320 nm wire width is fine.
3. The wire's edge overlaps the primitive's S/D vertical met1 frame
   (~75–100 nm of x-overlap) → same-layer same-net merge supplies
   the electrical tie. **The primitive's mcons feed S/D into that
   frame, and your wire merges into the frame from the side.**
4. If the wire start needs to reach the pin x (e.g. for a labeled
   tie or a horizontal jog into something off-axis), add a short
   met1 horizontal stub at `pin_y ± met1_half` spanning from the
   pin's li1 region out to the offset wire. Same met1, same net,
   they merge.

Burned on: T37 startup_circuit, routes #4 (MOSCAP array → GND
spine), #5 (INV_N.S → GND). Both had to be re-done after the
naïve pin-centered wire shorted S/D to the gate strip.

### Three routing helpers

```python
from rekolektion.layout import pin_patch, place_wire, place_via
```

**`pin_patch(sref, terminal)` → `PinPatch`** — closes the
electrical gap at a cell pin **for cross-row signal routing**.
`_core` primitives end at `li1`; routing onward through `met2`
needs a parent-painted met1 patch wide enough for via1's
asymmetric enclosure. The helper finds the labeled pin position
(`"D"` / `"G"` / `"S"` / `"B"`), paints the met1 patch, adds the
mcon contact to the underlying li1, and returns the patch
geometry plus the pin center in parent coords.

**Use `pin_patch` only for endpoints that need a via1 stack.** For
FET-to-rail connections, use `pin_to_rail` (next) — the std-cell
idiom there is li1 vertical, not met1 + via1.

```python
m5_d = pin_patch(srefs["M5"], "D")
m4_g = pin_patch(srefs["M4"], "G")
elements.extend(m5_d.elements)
elements.extend(m4_g.elements)
```

**`pin_to_rail(sref, terminal, dest)` → `list[Element]`** — ties a
FET S/D **directly to a power destination** with an li1 strap.
This is the std-cell idiom for VDD / VSS connections: contact
the FET pin through li1 vertically. Saves area and avoids the
via1 stack entirely.

`dest` selects the mode by its layer:

- **`li1` rect (preferred):** an existing tap strap. The helper
  paints an li1 extension that merges with it. **No new mcons**
  — the strap is assumed to have its own mcon stitch already
  (from `place_rail.stitch_li1_straps`).
- **`met1` rect:** a rail directly, no intermediate strap. The
  helper paints li1 + an mcon array in the overlap. Use only
  when there's no tap strap in the path — otherwise the extra
  mcons collide with the rail's existing tap stitch.

> **Requires a `_topgate` or `_botgate` primitive variant.**
> The default `topc=True, botc=True` primitives have gate contacts
> on both sides; the S/D li1 extension `pin_to_rail` paints will
> sit 60 nm from the opposite-side gate contact and fail `li.3`.
> Mint with `botc=False` (FET above rail) or `topc=False` (FET
> below rail) before connecting to a rail.

```python
# Preferred path: pin_to_rail to the tap band's li1 strap (no new
# mcons — the strap's existing rail stitch handles the rest).
elements.extend(
    pin_to_rail(srefs["M_NA"], "S", pwell_taps.li1_straps_by_side["bottom"][0])
)
elements.extend(
    pin_to_rail(srefs["M_PA"], "S", nwell_taps.li1_straps_by_side["top"][0])
)
```

`rail` accepts either an `rkt.Rect` (the rail's met1 rect, as
returned by `place_rail`) or a `(x1, y1, x2, y2)` bbox tuple. The
helper figures out whether the pin is north or south of the rail
and extends the li1 strap accordingly.

**Decision: when to use which.**

| Pin destination | Helper | Why |
| --------------- | ------ | --- |
| VDD or VSS rail (directly above/below the pin) | `pin_to_rail` | li1 vertical to rail, no met1 patch needed |
| Another cell pin (cross-row, cross-column) | `pin_patch` + `place_wire(met2)` + `place_via` | needs via1 stack for met2 routing |
| Another cell pin (same column, same well type) | `pin_to_rail` to a shared li1 strap, OR `pin_patch` if going through met2 | depends on routing channel availability |

If you find yourself using `pin_patch` for a pin whose only
destination is the VSS/VDD rail two µm away, stop — that's
`pin_to_rail`'s job. **And** make sure the FET on the other side
of that pin was minted with the right gate-contact topology
(`botc=False` for "FET over rail," `topc=False` for "FET under
rail") — otherwise `pin_to_rail` will fail `li.3`.

**`place_wire(start, end, layer, ...)` → `list[Element]`** —
paints a Manhattan wire (straight rect or L-shape) on `layer`.
Warns when the wire's direction conflicts with the layer's
preferred axis. Optional `via_to="met2"` adds a via stack at the
end point. Width defaults to the layer's minimum.

```python
# Horizontal met1 stub on the preferred axis — quiet.
elements += place_wire(m5_d.center, (m5_d.center[0] + 2000, m5_d.center[1]),
                       layer="met1", via_to="met2")
# Vertical met2 segment for cross-row.
elements += place_wire(
    (corner_x, m5_d.center[1]),
    (corner_x, m4_g.center[1]),
    layer="met2",
)
# Horizontal met1 landing onto m4_g.
elements += place_wire(
    (corner_x, m4_g.center[1]),
    m4_g.center,
    layer="met1",
    via_to=None,                # already on met1 patch
)
```

**Chain form — `place_wire([p1, p2, p3, ...], layer, ...)`.** Pass
a single list of points instead of `(start, end)`. The helper walks
them in order, emits one rect per straight run, and **collapses
collinear intermediate points into a single rect** rather than two
abutting ones.

Why this matters: a `met1_label`-driven flood-fill in Magic can
fail to traverse the seam between two abutting met1 polygons,
splitting what should be one logical net into two extracted nets
and failing LVS port-matching. The chain form sidesteps the seam
entirely by emitting one rect for a collinear run.

```python
# Connect three pin patches with one met1 wire — collinear points
# collapse into a single rect, so the gate_p label flood-fills
# across the whole wire.
gate_wire = place_wire(
    [p.center for p in gate_patches], layer="met1"
)
elements.extend(gate_wire)
```

Pairwise `place_wire(a.center, b.center)` + `place_wire(b.center,
c.center)` produces two abutting rects whose seam at `b` can stop
the flood-fill — use the chain form (or a single
`place_wire(a.center, c.center)` if the route is straight).

**L-shape corners need extended overlap.** The chain form (and the
2-point form's auto-L) emits each segment ending *exactly at the
corner point*. Each rect is min-width wide → half-width either side
of the centerline → at the L's corner the two rects share only a
**half-width × half-width** overlap (e.g. 85 × 85 nm for li1). That
overlap *is* sealed, but it forms a 45°-symmetric JOG that Magic's
DRC interprets as a sub-min-width "neck" at the concave corner —
trips `li.1` / `met1.1` even though both straight segments are
spec-width.

**Fix:** emit the L as two manual `place_wire` calls whose endpoints
each extend **one half-width past the corner point** along the
*other* segment's axis. That produces a full min-width × min-width
overlap and the JOG disappears.

```python
li1_w = 170  # SKY130 li1 min width
half = li1_w // 2
vertical = place_wire(top, (top[0], corner_y - half), layer="li1")
horizontal = place_wire((top[0] - half, corner_y), right, layer="li1")
elements.extend([*vertical, *horizontal])
```

Use the auto-L only when the corner is *interior* to a wider feature
that already seals it (e.g. landing on top of an existing strap or
pin patch); for free-standing L corners between two min-width
segments, paint the overlap explicitly.

**`place_via(point, from_layer, to_layer, cuts=(1, 1))` → `list[Element]`** —
paints a single via stack between two adjacent metal layers, with
the upper-layer enclosure rect. `cuts` controls the contact array
size (use larger arrays for power-strap stitches).

> **`place_via` does NOT paint the lower-layer rect.** The doc string
> says the caller owns it, "typically already painted as part of the
> wire or pin patch." When you drop a via1 directly onto a FET pin
> (without a `pin_patch`), the primitive's existing met1 contact strip
> is what catches the via — and that strip is sized for *mcon*
> enclosure, not via1. Sky130's via1 asymmetric rule (30 nm narrow +
> 60 nm wide, or thereabouts) is interpreted by Magic in a way that
> bare-strip enclosure trips `via.5a / via.4a` even when the strip is
> visibly wider than the cut. **Fix:** paint an explicit met1 landing
> pad at each via coord, **symmetric ≥0.10 µm enclosure on all four
> sides** (150 nm via1 cut → 350 × 350 nm pad). Symmetric is the
> reliable answer; asymmetric 30/60 nm encoded as a non-square pad
> still trips the rule when it overlaps the primitive's pre-existing
> met1 strip (the union polygon's step geometry confuses the check).

### Cross-row pattern (the common case)

The typical "connect an nfet pin to a pfet pin one row up" pattern:

```python
# 1. Patch both pin ends.
nfet_pin = pin_patch(srefs["M_NA"], "D")
pfet_pin = pin_patch(srefs["M_PA"], "G")

# 2. Route: short met1 stub out of nfet_pin, hop up to met2
#    (vertical preferred) for the cross-row span, hop back down to
#    met1 at the pfet end. place_wire's L-shape handles the corner.
elements.extend(nfet_pin.elements)
elements.extend(pfet_pin.elements)
elements.extend(
    place_wire(nfet_pin.center, pfet_pin.center, layer="met2")
)
# Add via1 stacks at both endpoints to bridge the met2 wire down to
# the met1 patches.
elements.extend(place_via(nfet_pin.center, "met1", "met2"))
elements.extend(place_via(pfet_pin.center, "met1", "met2"))
```

For a route that's purely on one axis (no row crossing), a single
`place_wire` on the appropriate-direction layer suffices — no via
stack needed.

### Composing sub-blocks — align pin Ys first, allocate tracks second

When the parent SRefs multiple sub-blocks side-by-side, the default
placement instinct is to **center each cell at parent y=0** (use
`oy = -(info.bbox[1] + info.bbox[3]) // 2`). That's clean visually
but it places different cells' *pins* at different parent Y values,
because each primitive's S/D pin local-Y is fixed:

- NFET S/D pins sit at cell-local y=0.
- PFET S/D pins sit at cell-local y=+180 (the bottom of the diffusion
  to S/D contact is offset from the cell origin).

So a bbox-centered NFET cell lands its S/D pin at OTA y=0, but a
bbox-centered PFET cell lands its S/D pin at OTA y=+97 (depends on
the PFET's bbox asymmetry). An inter-cell signal connecting an NFET
drain to a PFET drain across the row sees endpoints **97 nm apart in
y** — too small for a min-width wire to span without each endpoint's
via1 met2 enclosure (~160 nm half-extent) protruding into a concave
step that trips `met2.2`. The forced workarounds are ugly: a wide
rect spanning both via1 enclosures, or an L-corner with the JOG
sealed.

**Fix:** align cells by their **S/D pin Y**, not their bbox center.
At the parent placement step, set `sref_y = -pin_local_y` for each
cell so its S/D pin lands at the same parent Y (typically y=0):

```python
PIN_Y_OFFSET = {
    "nfet_cell":         0,    # NFET S/D pin at cell-local y=0
    "pfet_only_cell":  180,    # PFET S/D pin at cell-local y=+180
}
for name in order:
    oy = -PIN_Y_OFFSET.get(name, default_bbox_center)
    srefs[name] = rkt.SRef(cell=name, origin=(ox, oy))
```

With pin-Y aligned across cells, inter-cell signal nets become **clean
min-width horizontal wires** at a single Y — no wide rects, no L
corners, no Y-track allocation needed for those nets. Power rails
(VDD/GND) still need track allocation since they connect tap-strap
labels, not S/D pins, and tap straps are at very different Ys across
cell heights.

The cost: cells are no longer center-aligned by bbox, so the OTA's
visual centerline drifts slightly per cell. Acceptable for analog
where bbox centering wasn't load-bearing anyway.

### Composing sub-blocks — Y-track allocation for inter-cell nets

When pin-Y alignment isn't enough (different cells expose pins at
genuinely different Ys, e.g. multi-FET stage2 cells whose PMOS and
NMOS pins are far apart), the **all-at-y=0 trap** strikes: every
sub-block's external pins tend to sit at or near the FET pin y, so
naive horizontal wires for v_s_pair, ota_a, gate_pmos, etc. all
share the same Y band, overlap as met2 polygons, and merge into a
single short. Magic's extractor cheerfully reports one big net
containing every ostensibly-separate inter-cell signal — LVS rejects.

**Rule:** at the parent level, give each inter-cell net its own
horizontal Y-track. Choose tracks **outside the FET active y range**
(above the top tap strap, or below the bottom tap strap, or — for a
small number of nets — in the narrow safe band between the FET pin y
and the rail y). For each endpoint pin, jog vertically on the same
layer from the pin coord to the track Y before running horizontally.

```python
OTA_A_TRACK_Y = 1000   # above FET active, below VDD rail
GP_TRACK_Y    = -2800  # below FET active
VS_TRACK_Y    = -500   # just below FET pin y, clears ota_a's vertical jogs

ota_a_wire = [
    *vertical_segment(inp_ota_a[1], OTA_A_TRACK_Y, inp_ota_a[0]),
    *horizontal_segment(inp_ota_a[0], load_ota_a[0], OTA_A_TRACK_Y),
    *vertical_segment(load_ota_a[1], OTA_A_TRACK_Y, load_ota_a[0]),
]
```

**Watch the vertical jogs.** A vertical jog at x=X going from y=0 up
to y=TRACK is itself a met2 rect spanning x=[X-half, X+half] and
y=[0, TRACK]. If another net's horizontal wire at y=Y' crosses x=X
in the jog's y range, they merge. So pick each net's track y so that
no *other* net's track passes through the vertical jog's column. In
practice this means routing v_s_pair *below* y=0 (rather than above)
so the ota_a vertical jog from y=0 up to y=1000 doesn't cross
v_s_pair's track.

**Power rail rects must envelop their via1 enclosures.** A VDD or
GND rail that connects two endpoints at different y values is most
robustly painted as a **single wide met2 rect** spanning the bbox of
both endpoints — extended by the **via1 met2 enclosure half-extent
(~160 nm for via1)** past each endpoint y. Without that extension,
the via1 met2 enclosure rect at each endpoint protrudes past the
rail and creates a concave step, which trips met2.2 spacing inside
the same polygon. A min-width L-shape rail with the JOG sealed only
covers the rail itself — not the via1 enclosure padding around it.

### Routing order — sequence by topology

A block with more than a couple of internal nets needs a routing
*order*. Routing the easiest net first eats channels that the
hardest net then can't fit through; routing the hardest net first
overconstrains everything else. The convention that works:

| Phase | What | Why |
| ----- | ---- | --- |
| 1. Power | VDD, VSS, body taps | Already covered by `place_rail` + `place_taps_around` + `pin_to_rail`. Power needs the most real estate — do it first or it gets pushed into bad channels |
| 2. **Local abutting connections** | Within-row D-D or S-S between adjacent FETs (same row, same well) | These are *not really routing* — they're li1 strip merges. Two FETs whose drains share a net just need their D-strips to abut. Often zero new geometry, just primitive placement |
| 3. **Cross-row 2-pin nets** | A single nfet pin → a single pfet pin | One `pin_patch` + one `place_wire(layer="met2")` + via1 stacks at both ends. Predictable area cost |
| 4. **Multi-fanout nets** (3+ pins) | Nets touching 3 or more cell pins, often spanning multiple rows | Hardest. Need a routing "spine" (typically met2 trunk with met1 stubs branching). Save for last so you know where the obstacles are |

**Within each phase, tie-break by physical span: shortest first.**
A net whose pins are 2 µm apart routes trivially; a net spanning
the block width is much more constrained. Doing short ones first
leaves the wide channels open for the long ones.

**Classify by topology, not by signal name.** Whether a net is
called `drn_L` or `OUT` doesn't matter — what matters is "how
many pins, what rows do they sit in, can adjacent pins abut?"
Walk the net list once at the start, tag each net with its phase,
and route in phase order.

```python
# Pseudocode for the topology pass — agent-friendly.
for net_name, pins in nets.items():
    rows = {pin.row for pin in pins}
    if len(pins) >= 3:
        phase = 4  # multi-fanout
    elif len(rows) > 1:
        phase = 3  # cross-row 2-pin
    elif pins_are_adjacent_same_row(pins):
        phase = 2  # local abut
    else:
        phase = 3  # same-row but non-adjacent → treat as cross-row
    net_phase[net_name] = phase
```

**Fallback when a later phase can't route:**

1. Try a different layer for the blocked net (e.g. met3 if met2
   is full in that channel).
2. Add a met2 jog around the obstacle.
3. If neither works, move an earlier-phase route to free the
   channel. Don't move FET placement — re-route, not re-place.

Re-running `verify_drc` between phases catches violations early
when fixes are still cheap. Don't wait until every net is in.

**Phase boundaries are NOT check-in points.** Once routing has
started, walk the phases (2 → 3 → 4) to completion in a single
pass. Run `verify_drc` at each phase boundary, but do not stop to
ask the user "should I continue?" — that's the
`feedback_decision_vs_checkpoint` anti-pattern. Only halt when:

1. **DRC surfaces a violation that can't be fixed with a helper-
   call change** — i.e. the fix requires a real architectural
   decision (re-pick a layer, re-place a FET, abandon a topology).
   Those are decision points; ask.
2. **A net's required topology can't be expressed with the existing
   helpers** — file the helper gap and ask whether to work around
   or wait.
3. **The user explicitly asked you to stop after a specific phase.**

Otherwise: keep walking. Bugs, sizing tweaks, and natural pauses
between phases are not stopping points.

### Diagnosing met2 overlap conflicts before DRC

Raw `verify_drc` output gives you tile counts per rule but **no
spatial footprints** — when you see `met2.2: 24 tiles`, you have no
idea which drops are colliding.  Iterating routing changes against
unlocalized DRC counts is slow and blind.

`khalkulo/scripts/diagnose_met2_overlaps.py` is a static analyzer
that finds met2 overlap candidates from a viz geometry dump.  It:

- Loads the JSON produced by
  `mcp__rekolektion-viz__rekolektion_viz_get_geometry`.
- Filters to met2 rects (GDS 69/20).
- Reports pairs that **overlap in Y and have <140 nm gap in X**
  (the met2.2 spacing rule), skipping same-X-center stacks (drop
  wire + via2-widening on the same net).
- Infers each rect's net from nearby labels in priority order:
  met2_label inside bbox → met3_label at top/bottom Y (trunk
  landing) → met1_label at top/bottom Y (intra-bit bar landing
  per Hard Rule #13) → met1_label inside bbox → li1_label near
  bbox.
- Groups conflicts by net-pair so same-net intentional overlaps
  collapse and only **different-net conflicts** stand out.

Usage:

```bash
# 1. Snapshot geometry via the MCP tool. Output gets persisted to
#    a tool-results file because it's large.
# 2. Extract the inner JSON:
jq -r '.[].text' <viz-mcp-output-file>.txt > geom.json
# 3. Run the diagnostic:
.venv/bin/python khalkulo/scripts/diagnose_met2_overlaps.py geom.json
```

**Required setup for net inference to work:** any intra-bit metal
bar that ties multiple pins on the same net needs an
`internal=True` label on its drawing layer (e.g.
`rkt.Label(layer=met1_label_layer, text="cs_drain_0",
origin=(...), internal=True)`).  Internal labels appear in viz and
in the geometry dump but are not exported to GDS, so they don't
become subckt ports at LVS extraction.  Without bar labels every
intra-bit met2 vertical shows up as `?` and the conflict triage
collapses to "we can't tell what shorts what."

Run the diagnostic **after each routing iteration** that touched
met2 verticals — much faster than a Magic DRC round-trip and far
more actionable than tile-count summaries.

### NOT a valid pattern

**Routing everything on met1.** Every wire on one layer collides
with every other wire on that layer. The agent that did this in
the comparator block hit a ~12-net pileup that no amount of jog
geometry could fix. Use the layer alternation — it's the entire
reason multi-metal stacks exist.

**Skipping the met1 patch on cell pins.** `_core` primitives' S/D
li1 stubs are 230 nm wide; via1's enclosure rule demands ≥260 nm
along one axis and ≥320 nm along the other. Dropping a via1
directly onto an unpatched cell pin fails `via.1` / `met1.enclosure`
on every such pin. Always pin_patch first.

**`pin_patch` on a pin that already has met1 + mcon from the
primitive.** Some fixed-geometry primitives (PNPs, varactors,
certain caps) bake a met1 contact patch + mcon array into the
cell at the pin location. `pin_patch` paints another mcon on top
— with the primitive's mcons 100-200 nm away, the extra mcon trips
`mcon.2` (190 nm spacing) on every patched pin. Symptom: a tidy
`28 tiles: mcon.spacing < 0.19um` DRC failure right after adding
your routing.

**Fix:** for these primitives, skip `pin_patch` entirely. The
primitive already gives you met1 — just paint a `(label …)` on the
existing met1 polygon at the pin coord (via `inspect_primitive` →
`info.pin(name).origin` translated by the SRef origin). For pins
where the primitive provides only li1 (e.g. PNP Base / Collector),
label them on `li1_label` and let the parent's GND grid (or other
parent paint) handle the routing.

```python
# PNP-style: no pin_patch, just labels on the primitive's existing
# polygons. The PDK PNP already paints met1 over the Emitter and
# leaves Base/Collector on li1.
def pin_coord(sref, terminal, primitives_dir):
    info = inspect_primitive(sref.cell, primitives_dir=primitives_dir)
    pin = info.pin(terminal)
    return (sref.origin[0] + pin.origin[0],
            sref.origin[1] + pin.origin[1])

labels = [
    rkt.Label(layer=rkt.named("sky130", "met1_label"),
              text="v_be1",
              origin=pin_coord(q1_sref, "Emitter", primitives_dir)),
    rkt.Label(layer=rkt.named("sky130", "li1_label"),
              text="GND",
              origin=pin_coord(q1_sref, "Base", primitives_dir)),
    # … and so on for Collector, Q2's pins, …
]
```

The general rule: `pin_patch` is for FET-style pins that come out
on li1 only and need a via1 stack added. If the primitive already
provides met1 (check the `.rkt` for `met1` rects near the pin
label), don't double-stack.

## Naming nets — DON'T skip this step

A `.rkt` block that has SRefs and parent-paint geometry but **no
labels** is not done — even if it renders correctly. The viz tool's
net view, the ratline overlay, the LVS flow, and the sidecar JSON
all key off **labels** to figure out which polygons belong to which
electrical net. Without labels:

- The power rails you painted at the top and bottom of the block
  have no name and don't appear as nets at all.
- LVS will fail port matching against the reference SPICE.

### Label kinds — `NetName`, `DeviceTerminal`, `PortName`

Every label carries an intrinsic **kind**:

| Kind             | Meaning                                                   | Who sets it                                          |
| ---------------- | --------------------------------------------------------- | ---------------------------------------------------- |
| `NetName`        | The label's text is a signal or power net name.           | Default for any new label                            |
| `DeviceTerminal` | A FET port annotation (`D`/`G`/`S`/`B`).                  | FET generator (`gen_nfet_hv` / `gen_pfet_hv`) at mint time |
| `PortName`       | A hand-authored sub-block's external port (e.g. `A`/`B`/`Y` on `nand2`). | Sub-block author tags explicitly via `port_label(…)` or `(kind port-name)` |

In the `.rkt` source the three look like:

```scheme
(label (layer sky130:li1_label) (text "D") (origin -395 0)
  (kind device-terminal))     ;; emitted by gen_nfet_hv
(label (layer sky130:met1_label) (text "A") (origin 120 80)
  (kind port-name))           ;; emitted by sub-block builder
(label (layer sky130:met1_label) (text "VDD") (origin 7995 5870))
                              ;; no (kind …) → defaults to net-name
```

**Why three kinds?** Net-level consumers (ratlines, LabelFlood)
skip both `DeviceTerminal` AND `PortName`. The difference is
*who tags*:

- FET primitives → tagged by the generator. You never write
  `(kind device-terminal)` yourself.
- Hand-authored sub-blocks (`nand2`, `lshift_1v8_to_3v3`, etc.) →
  tagged by **you** when you author the sub-block. Use the
  `port_label(…)` Python sugar, or write `(kind port-name)` if
  you're hand-authoring `.rkt` text.

Without `PortName`, sub-block port labels default to `NetName`,
and the second you SRef the sub-block twice you get two
instances'-worth of "A" labels aliasing into one fake "A" net
with a spurious ratline between them. Tagging port labels fixes
it the same way `DeviceTerminal` fixed it for FETs.

**Hand-authoring rules:**

- Never write `(kind device-terminal)`. The FET generator does it.
- Always tag a sub-block's external port labels (`port_label(…)`
  in Python, or `(kind port-name)` in raw text). One tag per port,
  once, when you author the cell.
- Every other label you paint at the block level is `NetName` by
  default — which is what you want.

### Composing hand-authored sub-blocks

When you build a sub-block (a `nand2`, an inverter chain, a level
shifter, whatever), the labels you place at its external pins are
its public interface. The block above will SRef this cell and
expect to connect to "A", "B", "Y" by position. Two patterns to
follow:

**Pattern 1: Python builder script.**

```python
from rekolektion.io import rkt

doc = rkt.Document(
    cells=[
        rkt.Cell(name="nand2", elements=[
            # Port labels — tagged PortName so they don't alias.
            rkt.port_label(rkt.named("sky130", "met1_label"),
                           text="A", origin=(120, 80)),
            rkt.port_label(rkt.named("sky130", "met1_label"),
                           text="B", origin=(120, 240)),
            rkt.port_label(rkt.named("sky130", "met1_label"),
                           text="Y", origin=(400, 160)),
            rkt.port_label(rkt.named("sky130", "met1_label"),
                           text="VDD", origin=(0, 320)),
            rkt.port_label(rkt.named("sky130", "met1_label"),
                           text="VSS", origin=(0, 0)),

            # Optional: an internal net for traceability. Default
            # NetName + internal=True keeps it visible in viz but
            # absent from the GDS Magic reads (so LVS doesn't see
            # a spurious 6th port).
            rkt.Label(layer=rkt.named("sky130", "met1_label"),
                      text="nand_mid", origin=(260, 160),
                      internal=True),
            # … geometry rects, SRefs, etc. …
        ]),
    ],
)
```

**Pattern 2: hand-authored `.rkt` text.** Same idea, but you write
`(kind port-name)` on each port label by hand.

```scheme
(cell nand2
  (label (layer sky130:met1_label) (text "A") (origin 120 80)
    (kind port-name))
  (label (layer sky130:met1_label) (text "B") (origin 120 240)
    (kind port-name))
  ;; …
  (label (layer sky130:met1_label) (text "nand_mid") (origin 260 160)
    (internal #t))
  ;; …
)
```

**The parent's view (after SRef'ing this `nand2` twice):**

- Two `(sref (cell nand2) …)` instances at different origins.
- Each instance's "A"/"B"/"Y"/"VDD"/"VSS" labels are `PortName` →
  net consumers skip them entirely. No ratlines, no aliasing.
- The parent paints its own `NetName` labels on the wires that
  feed each instance (e.g. `pulse_kind_form_1v8` on the wire to
  `nand2[0].A`). Those are the nets the viz / LVS sees.
- `nand_mid` is `NetName` + `IsInternal` → visible in viz as
  "this sub-block has an internal net `nand_mid`", but not in
  GDS, so LVS doesn't extract it as a phantom port.

### Migrating existing hand-authored sub-blocks

Existing sub-blocks (`nand2`, `lshift_1v8_to_3v3`, anything else
you built before `PortName` existed) have port labels defaulting
to `NetName`. The fix is per-cell: edit the builder script to use
`port_label(…)` for port labels, or hand-edit the `.rkt` to add
`(kind port-name)` after the `(origin …)` form. Re-run any
downstream blocks that SRef the updated cell — no other change
required; the new format is backward-compatible (legacy `NetName`
parses cleanly, just aliases the way it always did until you
re-tag).

### What still requires you to label something

The kind model fixes the *spurious* nets (FET terminals and sub-
block ports showing up as if they were nets). It doesn't conjure
*real* nets out of nothing — those still need parent-painted
labels:

| Net the block needs | What you do |
| ------------------- | ----------- |
| Power rail (VDD/VSS) | Paint a label on the rail's met1 — see below |
| Internal signal (cross-FET, cross-sub-block wire) | Paint a label on the parent-paint routing wire. If the net is parent-private (doesn't escape to a port), add `(internal #t)` |
| Reusing a FET pin as a named net | Paint a `NetName` label at the pin location (won't collide with the primitive's `DeviceTerminal` label — different kind, same string is fine) |
| Reusing a sub-block port as a named net | Same — paint a `NetName` label at the SRef's port location; it overrides the sub-block's `PortName` for parent-level net visibility |

### How to label power rails

The minimum for a block with VDD and VSS supply rails:

```scheme
(cell my_block
  ;; Bottom rail — VSS
  (rect  (layer sky130:met1)        0 -1000 15990 -200)
  (label (layer sky130:met1_label) (text "VSS") (origin 7995 -600))

  ;; Top rail — VDD
  (rect  (layer sky130:met1)        0 5470 15990 6270)
  (label (layer sky130:met1_label) (text "VDD") (origin 7995 5870))

  ;; … sref + signal wiring …
)
```

**Layer rule:** drawing geometry goes on `sky130:met1` (GDS 68/20),
labels go on `sky130:met1_label` (GDS 68/5). The same pattern holds
for `met2`/`met2_label`, `li1`/`li1_label`, etc. Mixing them up — a
label on the drawing layer — renders fine but loses the LVS hook.

**Origin rule:** the label's origin must sit **inside** the
drawing-layer polygon it's naming. A common safe choice is the
polygon's centroid. The flood-fill that powers nets-from-labels
treats any drawing polygon that overlaps the label's origin as
belonging to that net.

### How to label signal nets

For internal signals — diff-pair inputs, bias rails, output —
follow the same pattern, but the label sits on whatever metal
layer the parent-paint wire uses. Example for a current-mirror
bias rail running across a few pfets:

```scheme
;; Parent-paint wire on met1 connecting three pfet gates.
(rect  (layer sky130:met1) 4000 3800 13000 3950)
(label (layer sky130:met1_label) (text "VBIAS_P") (origin 8500 3875))
```

The single `VBIAS_P` label makes the wire — plus anything that
flood-fills to it (other met1 on the same net, via stacks down to
gates) — show up as one net everywhere it appears.

### Labeling a primitive's pin — use the extracted PORT position

When you put a parent-level label directly over a primitive's `D` /
`S` / `G` pin (rather than on a parent-painted wire), the label
must sit on the **primitive's extracted port position**, not on the
primitive's own label position.  These can differ by ~100 nm.

Magic's port-promotion only merges a parent label with an SRef's
internal pin when the label coord matches the port's tile coord.
If the label is on the same `li1` polygon as the strap but at a
different y from the port tile, the label becomes a **floating
port** — visible in the extracted netlist's `.subckt` line but not
electrically connected to any device.  LVS then reports a
"port matching" failure even though the topology is correct.

**Concrete example — `n_w12_l1` (W=1.2 nfet HV):**

| Position kind                 | Cell-local | Source                  |
| ----------------------------- | ---------- | ----------------------- |
| Primitive's own `G` label     | (0, 620)   | `gen_nfet_hv` output    |
| Primitive's extracted G port  | (0, 720)   | `cellname.ext`          |

A parent label at (0, 620) sits on the li1 strap (which spans
y=535..705) but doesn't merge with the G port (which Magic places
at y=720 on `met1` above the strap).  A parent label at (0, 720)
merges correctly.

**How to find the port position.** After one round of
`verify_lvs`, look in the extracted `<primitive>.ext` file:

```
$ grep "^port " /path/to/lvs-output/nfet_hv_W1p2_L1p0_core_topgate.ext
port "S" 2 129 -31 129 -31 li
port "D" 1 -129 -31 -129 -31 li
port "G" 3 0 144 0 144 li         ← G port at Magic (0, 144) = (0 nm, 720 nm)
```

Multiply Magic coords by 5 to get nm (sky130 magic uses 1 unit =
0.005 µm).  Use those coords for the parent label.

**Don't trust the primitive's `.rkt` label position.** The
generator places its own `(label …)` for the device-terminal name
(`D`/`S`/`G`/`B`), but Magic's port extraction can pick a tile on
an adjacent stacked layer (met1 over the li1 strap) with a small
y-offset.  The `.ext` is the ground truth.

The D/S ports usually coincide with the cell's label position for
HV FETs; G ports for wider devices are the ones to watch.

### No `(nets …)` block — labels are the net set

The `.rkt` format has **no separate net declaration block**. Don't
write `(nets …)` blocks at the top of your file; the parser
silently ignores them and the format has no field to land them in.
The label set IS the net set. Power classification is heuristic
(VPWR/VDD → power, VSS/GND → ground, CLK* → clock, else signal)
based on the label's text.

If you previously wrote a `(nets …)` block by hand, delete it —
the file is shorter, the format is simpler, and downstream
consumers find the same nets via labels.

### Sanity check

After labeling, re-run `viz read` and confirm the cells loaded.
Then open the block in `app`, switch to the Nets tab, and check
that:

1. Power rails show up as named nets (`VDD`, `VSS`, …).
2. Each signal you intended exists with the expected pin count.
3. **No nets named `D`, `G`, `S`, or `B`** — the kind filter
   should drop those FET-terminal labels. If you see one in the
   Nets list, a primitive was generated with an old generator
   that didn't tag.
4. **No nets named after a sub-block's port** (`A`, `B`, `Y`,
   etc. depending on the sub-block) — same idea but for
   `PortName`-tagged labels. If you SRef a `nand2` twice and see
   a net called `B` with pins at both instances, the sub-block's
   port labels aren't tagged `PortName`. Fix by re-emitting the
   sub-block with `port_label(…)` (or hand-editing its `.rkt` to
   add `(kind port-name)`).

If 1 or 2 fail, the labeling is incomplete. If 3 or 4 fail, a
sub-block needs re-tagging. Fix and re-check before declaring
the block done.

## Verifying your block

Verification is a **three-step gate** — a block is not done until
all three pass:

1. **Geometric / structural** (`viz read`, `viz app`): "is the
   geometry where I think it is, do the cells load, are the bboxes
   reasonable?" Fast, no PDK needed.
2. **DRC** (`verify_drc`): "does Magic agree this is manufacturable
   silicon?" Slow (~30 s per block), needs PDK + Magic installed.
3. **LVS** (`verify_lvs`): "does the extracted netlist match the
   reference schematic?" Slow (~30–60 s per small block), needs
   PDK + Magic + netgen installed. **Required before committing a
   new block.** DRC-clean does NOT imply LVS-clean — see Step 3 for
   why.

### Step 1 — geometric (`viz read` / `viz app`)

The viz CLI exposes six verbs; the two you'll use most are `read`
(fast summary, no GUI) and `app` (interactive GUI). **All viz verbs
accept `.rkt` directly. Never `to-gds` your own work-in-progress
block to view it** — that produces a frozen snapshot disconnected
from your subsequent `.rkt` edits. The same trap applies to static
`viz-render` PNGs: the live `app` GUI (or the MCP
`rekolektion_viz_open` + `rekolektion_viz_screenshot`) is the
canonical placement-review surface for in-flight work.

| Verb         | What it does                                                   |
| ------------ | -------------------------------------------------------------- |
| `read`       | Text summary of a `.rkt` or `.gds`: cell count, poly/path/sref totals, per-cell bbox |
| `to-gds`     | Export `.rkt` → canonical sky130 GDS (used by `verify_drc` / `verify_lvs` / tape-out; **NOT** for viz review of your own block) |
| `app`        | Launch interactive 2D + 3D Avalonia viewer (pass `.rkt` for in-flight work; `.gds` for tape-out / vendor IP) |
| `render`     | Per-layer PNG export (legacy; GDS input only)                  |
| `mesh`       | STL + GLB 3D model export                                      |
| `viz-render` | Headless one-shot render to a single PNG (for CI / agents)     |

```bash
# Fast text summary — verify cells loaded, bbox is sensible
dotnet run --project tools/viz/src/Rekolektion.Viz.Cli -- \
    read cell_designs/my_group/my_block.rkt

# Interactive 2D + 3D viewer
dotnet run --project tools/viz/src/Rekolektion.Viz.Cli -- \
    app cell_designs/my_group/my_block.rkt
```

The viz tool's LayoutLoader walks the import graph, merges cells,
and renders the composed silicon-truth view. If an import is
unresolved, you get a warning, not a crash — the missing primitive
just shows up as an empty SRef bbox.

**Bbox interpretation:** the per-cell bbox `read` prints covers
only that cell's *direct* parent-paint elements (rects, polys,
paths). **SRef instances do NOT contribute to the containing cell's
bbox in `read` output** — the primitives' bboxes are listed on
their own rows. So a block whose only parent-paint is one met1
strap will show a bbox the size of that strap, regardless of how
big the SRef'd primitives underneath it are. To see the flattened
composed extent, open it in the GUI or use `viz-render`.

### Step 2 — DRC (`verify_drc`)

```python
from rekolektion.verify import verify_drc

result = verify_drc("cell_designs/my_group/my_block.rkt")
print(result.summary())
if not result.clean:
    for err in result.real_errors:
        print(" ", err)
```

`verify_drc` converts the block to GDS via the viz CLI's `to-gds`
verb, loads it into Magic, runs `drc check` against the
`sky130B.tech` deck, parses the violation report, and returns a
`DRCResult`. **A block that `viz read` accepts but `verify_drc`
fails is NOT done.** Common failure modes that `viz read` never
sees but DRC catches:

- `nwell.2a` (no-man's-land between adjacent same-type wells)
- `via.1` / `met1.enclosure_of_via1` (cell pin not patched before via)
- `tap.5` (well floating from supply — missing taps or unstitched rail)
- `psdm/nsdm.2a` (implant spacing)
- `met1.1` / `met1.2` (wire width / spacing on the routing layers)

**Iterate until clean.** If `result.real_errors` is non-empty, fix
the helper call that produced the bad geometry — don't patch the
output. Every real DRC violation traces back to one of:

- Picked a placement gap that's in the abut-or-tub no-man's-land
  → use `place_row` or `place_tub` instead of hand origins.
- Routed onto an unpatched pin → call `pin_patch` first.
- Routed against a layer's preferred direction at length → switch
  layers.
- Skipped well taps or the rail stitch → use `place_taps_around` +
  `place_rail`.

DRC is slow (~30 s per block), so don't gate every edit on it — but
**always run it before LVS.** Geometry must be manufacturable before
electricals matter; iterating LVS on geometry that DRC would have
flagged wastes the longer LVS cycle.

### Step 3 — LVS (`verify_lvs`)

```python
from rekolektion.verify import verify_lvs

result = verify_lvs(
    "cell_designs/bl_clamp/blc_comparator.rkt",
    "cell_designs/bl_clamp/blc_comparator_sch.spice",
    cell_name="blc_comparator",
)
print(result.summary())
if not result.match:
    print("netgen log:", result.log_path)
```

`verify_lvs` converts the block to GDS, extracts a SPICE netlist
via Magic (`ext2spice`), and compares the extraction against the
reference schematic using netgen's batch-LVS mode.  Returns an
`LVSResult` with `.match` (bool) and `.log_path` (netgen comparison
log).

**Why a separate gate?** DRC checks polygons; LVS checks the
electrical net graph.  A block can be DRC-clean while:

- A net's label sits on a polygon that's **electrically disjoint**
  from the rest of the net (e.g. a Phase-2 abut bridge was forgotten
  and two same-named islands exist).
- A via stack lands on the **wrong met1 polygon** (right geometry,
  wrong net).
- A primitive's terminal isn't actually shorted to its sibling
  (e.g. M3's unlabeled `D1` strip not actually tied to `D0` even
  though both carry the same parent-level label by intent).
- An nfet's bulk is connected to VDD instead of VSS through a
  miswired tap.

All of these pass `verify_drc` and fail `verify_lvs`.

**Common LVS failure modes and their fixes:**

| Symptom | Root cause | Fix |
| ------- | ---------- | --- |
| "net X not found in layout" | label exists but flood-fill can't reach a port | add a parent-paint wire merging the two islands; or correct the label position |
| "extra net in layout" | un-named polygon picked up by extraction as a floating net | label the polygon, or short it to the intended net with parent paint |
| Gate net merges with body net in extracted layout (`X*/G` shares a node with `X*/B`); merge chain looks like `instance/G ↔ m1_<n>_<n># ↔ instance/B ↔ <body_label>` with no-attr middle merges that look like a Magic hierarchical-port-promotion bug | NOT a Magic bug. The primitive's bottom (or top) gate li1 strap overlaps the body tap's li1 strap on the same plane — silent geometric merge, hidden behind primitive-footprint DRC waivers. See **Hard Rule #20** | re-mint the primitive with `botc=False` (for south taps) or `topc=False` (for north taps), update the build script to read the tap's `inner_bbox` from the actual primitive diff bbox (which is asymmetric in the gate-stub-stripped variants), DRC + LVS re-verify |
| "port matching failed" with port present in both .subckt lines, but `node "X" 0 0 …` in the layout `.ext` (zero tile count) | parent label is on the primitive's li1 strap but offset from the primitive's extracted *port* tile by ~100 nm — Magic creates a floating port instead of merging with the SRef pin | move the label to the port coord from the primitive's `.ext` (`grep "^port " <prim>.ext`).  See **Labeling a primitive's pin** in the Naming nets section |
| "device mismatch — M3 W/L differs" | called a generator with wrong params | re-mint the primitive with the schematic's parameters |
| "extra device — unexpected nfet" | a primitive SRef'd by accident, or a guard-ring variant minted instead of `_core` | drop the extra SRef; check `guard=` |
| "port mismatch — schematic has VDDA1 layout has VDD" | schematic and label disagree | rename the label, or update the schematic — pick whichever matches the SoC integration spec |
| "extra port — `src_node` in layout but not schematic" | internal-only net got labeled at the parent level, and `make_ports=True` promoted it | omit parent labels for internal-only nets; LVS will match them by topology |
| "device mismatch — instance has named pins Emitter/Base/Collector vs positional 1/2/3 with proxy pins" | The PDK black-box device (PNP, NPN, varactor, …) carries **named** pin labels in its `.mag`; Magic's extraction preserves the names. Your schematic calls the device by positional pin order, so netgen can't align them and adds `proxyEmitter` / `proxyBase` / proxy-numbered pins to both sides | Add **stub `.subckt` declarations** with matching named pins to your reference SPICE. For each PDK device used: `\.subckt sky130_fd_pr__rf_pnp_05v5_W0p68L0p68 Emitter Base Collector` `.ends`. The schematic's `X` line stays positional (`XQ1 v_be1 GND GND sky130_fd_pr__rf_pnp_05v5_W0p68L0p68` — order matches the stub's pin list), but netgen now sees `Emitter`/`Base`/`Collector` as proper port names on both sides. No PNP model body needed (it's a black-box for LVS — model parameters are checked elsewhere). |
| "device mismatch — resistor instance has positional `(1,2)`, `3` vs schematic stub's `r0`/`r1`/`b` with proxy pins on both sides" | Opposite trap to the PNP/NPN case. Magic extracts `res_xhigh_po` (and other PDK resistors) with **positional** pins `1, 2, 3` — there are no named pin labels in the `_core` resistor's `.mag`. If you add a named-pin stub (`.subckt sky130_fd_pr__res_xhigh_po_1p41 r0 r1 b`), netgen synthesises proxies on both sides (`proxy1/proxy2/proxy3` on schematic, `proxyr0/proxyr1/proxyb` on layout) and the instance pin counts diverge (5 vs 6) | **Remove the resistor stub entirely.** With no stub, netgen black-boxes the model and matches purely positionally — and `sky130B_setup.tcl` already declares `permute "-circuit2 $dev" 1 2` for the resistor device class, so R1/R2 commutativity is handled. Rule of thumb: PDK devices whose `_core` primitive uses **positional** terminal numbers (resistors, mim caps) → no stub. PDK devices whose `_core` carries **named** labels (BJTs, varactors) → named stub. Inspect `<primitive>.ext` for `port` lines if unsure. |
| "Cell X disconnected node: PORT_NAME" for a port on a transformed SRef cell + simultaneous "Netlists match uniquely with port errors" + duplicate port entries in the layout side of `comp.out` | `reflect`/`rot` semantics mis-applied on the SRef; parent labels and wires were placed at coords computed under the wrong transform (typical mistake: treating `reflect=True` as X-flip when it's actually Y-flip).  Magic's hierarchical extract resolves the SRef geometry correctly, so the labels hover over wrong/empty polygons.  BSIM4 S/D symmetry lets netgen find an isomorphism that masks the structural mismatch — you read "matches uniquely" as a green light when it's the signal that S/D swap is hiding a real bug | Re-read **SRef transforms** above.  `reflect=True` alone is Y-flip; for X-flip use `reflect=True + rot=180°`.  Recompute every parent pin coord via the composed matrix, move labels and wire endpoints, re-verify.  Then run **flat-extract LVS** (below) to confirm silicon connectivity independent of the hierarchical extractor.  **DO NOT** paper over with `port_aliases` (it's not a vsrc-naming alias) or parent-paint patches (they create disconnected islands, not merges through SRef boundaries) |

**Iterate until match.** As with DRC, every LVS failure traces back
to one of: a missing parent-paint connection, a mis-positioned
label, a wrong generator param, or a schematic that doesn't match
intent.  Don't patch the extracted netlist; fix the source.

**The reference schematic.** LVS needs a `<block>_sch.spice` (or
similar) that you author or get from the analog designer.  It
declares the same FETs, with the same W/L and same net names, that
your `.rkt` is supposed to instantiate.  Without a reference, LVS
has nothing to compare against — you'd be running an extraction
only, which catches some classes of error (e.g. floating nets) but
not "wrong topology."

**Port aliases — when LVS sees a V_TIE-style alias.** Some
schematics use a 0 V source (e.g. `V_TIE NODE_A NODE_B 0`) to
declare two port names as the same physical node, for clarity at
the SoC boundary.  netgen removes zero-vsrcs as a normalization
step, leaving the second name as a floating port that fails
top-level pin matching — even though the topology is identical.

`verify_lvs` accepts a `port_aliases=[(layout_name, schematic_name), ...]`
argument that resolves this by rewriting the reference schematic
on the fly: dropping the V_TIE source and renaming the port in the
`.subckt` header.  This is a **structured waiver, not a workaround**
— the shim enforces a strict safety contract:

1. **Verified.** The schematic must contain exactly one 0 V source
   between the two named nodes.  If absent, the alias is rejected.
   This means you cannot fudge LVS by declaring two unrelated
   ports as equivalent.
2. **Constrained.** Only two line-level changes are allowed in the
   rewritten file: the `.subckt` port-list rewrite and the V_TIE
   removal.  Any other delta aborts the call.
3. **Auditable.** The rewritten schematic is saved alongside the
   original (`<schematic>_lvs_aliased.spice`) and returned in
   `LVSResult.aliased_schematic_path`.  Future readers can diff
   against the original.
4. **Caller-scoped.** Aliases are passed at the `verify_lvs` call
   site, in the block's build script.  Not in a global config that
   silently affects other blocks.
5. **Visible in the result.** `LVSResult.port_aliases_applied`
   lists every alias that ran, and the `summary()` headline
   includes them: `LVS MATCH: blc_trim_ref [port aliases: V_BIAS_TRIM↔BLC_REF_5UA]`.

Example:

```python
result = verify_lvs(
    "cell_designs/bl_clamp/blc_trim_ref.rkt",
    "cell_designs/bl_clamp/blc_trim_ref_sch.spice",
    cell_name="blc_trim_ref",
    port_aliases=[("V_BIAS_TRIM", "BLC_REF_5UA")],
)
```

What this does NOT bypass: device count, parameter values, net
topology, or any electrical correctness check.  The shim only
mediates a SPICE-naming convention that doesn't survive netgen's
vsrc normalization.

**Announce the gate-3 pass explicitly.** When `verify_lvs` returns
`LVS MATCH`, the very next user-facing line must be a HEADLINE
acknowledging that all three verification gates closed:

> **LVS MATCH — gate 3 passed.** Block is DRC + LVS clean against
> `<block>_sch.spice`.  Ready to commit.

Don't bury the LVS result inside a paragraph about block dimensions
or routing topology.  The user (and future-you) should be able to
glance at the close of the conversation and see the three-gate
clearance.  Burying it leads to "did LVS actually run?" callouts.
Move-to-next-block / commit / push only after this headline appears.

### Flat-extract LVS — verification for cells with reflected SRefs

Magic's hierarchical port-promotion has a known bug with reflected
SRefs (see `rekolektion/CLAUDE.md` trap *"Magic ext2spice
port-promotion through hierarchy is broken"*).  Even after the
*SRef transforms* fix (correct reflect/rot matrix interpretation),
specific reflect+rot combinations can fail to propagate parent
labels through to the reflected primitive's port.  Symptoms in
netgen LVS:

- `"Cell X disconnected node: PORT_NAME"` for ports on reflected
  sub-cells, even though the parent label sits on the right
  polygon geometrically.
- `"Netlists match uniquely with port errors"` — netgen found an
  isomorphism via S/D-symmetric matching but the named ports don't
  align.

The verification fallback: **flat-extract LVS**.  Force Magic to
flatten the cell hierarchy BEFORE extracting, so there are no SRef
boundaries left for port-promotion to fail at.  Parent-paint
polygons and the primitive's strap, both on the same layer at the
same geometric position, merge unambiguously in the flat extract —
and Magic's port association works.

**When to run flat-extract LVS:**

- Every cell that uses `reflect=True` on its SRefs, as a secondary
  check supplementing hierarchical LVS.
- Whenever hierarchical LVS reports `"Netlists match uniquely with
  port errors"` — the unique-match phrase means topology is right
  but the port story is ambiguous.  Flat extract resolves the
  ambiguity.
- As primary verification for blocks where the hierarchical
  extract is suspected of mis-grouping nets across SRef boundaries.

**Core Magic TCL** (insert a flatten loop before `extract all`):

```tcl
gds read <gds_path>
load <cell_name>
select top cell
foreach _c [cellname list children <cell_name>] {
    if {$_c eq "(UNNAMED)"} { continue }
    flatten -doinplace $_c
}
select top cell
extract all
ext2spice lvs
ext2spice -o <output_spice>
```

Then run netgen as usual against the schematic.  A reference
implementation lives at
`khalkulo/scratch/t37_stage2_layout/verify_lvs_flat.py` —
intended to be promoted into `rekolektion.verify` once stable.

**Important:** flat extract is for VERIFICATION, not for normal
LVS.  Hierarchical extract is faster and produces cleaner `.ext`
files when it works.  Use flat extract as the gold-standard
fallback when reflect-related ambiguity needs resolution, not as a
default.  A flat-extract MATCH is the strongest available
guarantee that the silicon connectivity matches the schematic — a
hierarchical MATCH is the normal expectation but can be misleading
in the presence of the trap.

**Sign-off rule for cells with reflected SRefs:** the three-gate
gate-3 headline must reference BOTH hierarchical AND flat-extract
LVS results.  Example:

> **LVS MATCH — gate 3 passed.** Block is DRC + LVS clean against
> `<block>_sch.spice` (hierarchical extract).  Flat-extract LVS
> also clean — silicon connectivity verified independent of the
> SRef hierarchy bug.

---

## Where files live

| Path | What |
| ---- | ---- |
| `src/rekolektion/primitives/sky130/` | Generator Python (fet.py, ...) |
| `src/rekolektion/layout/placement.py` | `place_row`, `place_tub` helpers |
| `src/rekolektion/layout/taps.py` | `place_taps_around` |
| `src/rekolektion/layout/rail.py` | `place_rail` (rail + auto-stitch) |
| `src/rekolektion/layout/routing.py` | `pin_patch`, `place_wire`, `place_via` |
| `src/rekolektion/tech/sky130.py` | `ROUTING_DIRECTION`, `ROUTING_PITCH_UM`, `Axis` |
| `src/rekolektion/verify/rkt_drc.py` | `verify_drc(rkt_path)` — Magic DRC integration |
| `src/rekolektion/io/rkt.py` | Python writer for `.rkt` |
| `cell_designs/primitives/` | PDK-minted primitive cells (auto-generated on first generator call, **commit to git**) |
| `cell_designs/<group>/<block>.rkt` | Hand-/Python-authored blocks |
| `tools/viz/src/Rekolektion.Viz.Core/Rkt/` | F# reader/writer/types |
| `docs/io/rkt.md` | Full schema reference |
| `docs/workflows/rkt_primitive_workflow.md` | This doc |

Commit primitive `.rkt`s to git: they're reproducible artifacts (same
params → same digest → same content), but committing them means:

- Anyone can build the project without Magic installed
- `git blame` shows when each primitive was minted and against which PDK
- A primitive change is reviewable as a diff

---

## Methodology lessons

These are recurring practices that worked across multi-cell blocks
(Phase A srcmux, …) when the naive approach didn't. **Read the
[SKY130 DRC cheatsheet](../operational_protocols/sky130_drc_cheatsheet.md)
for the rule-level gotchas referenced below.**

### Strip down when DRC accumulates errors across layers

When a fully-routed block returns dozens to hundreds of DRC errors,
**don't try to fix them in place**. Errors from placement, power
routing, signal routing, vias, and labels become impossible to
attribute. Instead:

1. Strip the build script back to **placement only** (SRefs +
   parent-painted wells/bulk paints, no routing, no labels).
2. DRC. Errors here are all placement / nwell-merging issues.
3. Add **one power net at a time**: VSS → DRC → VDDA1 → DRC → VDD
   → DRC → SRC_RAIL → DRC. Each new error attaches to the just-added
   geometry.
4. Add internal signal nets next, one or two at a time, DRC between.
5. Add port labels last; they don't change DRC but do shape LVS.

When the original Phase A srcmux build hit 102 errors after the
discharge cell was dropped in, this strip-down took it back to a
clean 0-error placement baseline in one pass and surfaced *which*
routing pieces caused which problems on the way back up.

### Step-by-step DRC validation per net

Even within power or signal routing, **don't batch additions**. A
single new net or even a single new via stack between DRC runs gives
you immediate attribution. Magic DRC on a small block is < 5 s; the
turn-around cost is negligible.

### NWELL cutouts for body-bias-mismatched cells

When a sub-cell needs a different body bias than the surrounding
nwell strip (e.g., a 1.8 V PFET sitting amid VDDA1 = 3.3 V strips):

1. **Don't merge.** Different voltages on the same nwell = short.
2. **Cut the strip around the cell** with `nwell.2a` setback
   (1.27 µm from the cell's intrinsic nwell on each side).
3. **Verify the strip's previous merging still holds** through other
   cells whose internal nwells bridge the cut. If not, add a small
   nwell bridge at a clear y above or below the cell.

If the cell needs a body tap at a *different* voltage than the strip
(common case: 1.8 V PFET into VDD nwell, surrounded by 3.3 V VDDA1
strips), see "Body taps for primitives without internal taps" below.

### Body taps for primitives without internal taps

The PDK's `_core` (non-guard) 1.8 V FET primitives **don't include
an internal body tap**. The cell's nwell extracts as a floating
node, LVS reports one extra net.

Three options when you hit this:

| Option | When to use |
|---|---|
| **Parent-paint a body tap** | Default. Extend the cell's nwell into a free area, add the standard n+ tap stack, tie to the bulk net. See the [cheatsheet](../operational_protocols/sky130_drc_cheatsheet.md#diffusion-taps--licon7-and-li5-need-bigger-enclosures) for the layer stack and enclosure thresholds (`licon.7` = 120 nm, `li.5` = 80 nm). |
| **Use `guard=True`** | When the cell footprint can grow significantly (~2×). Built-in body ring. |
| **Bias to adjacent strip voltage** | Only when the resulting body bias is within the device's reliability spec (e.g., +1.5 V reverse bias on a 1.8 V PFET adds Vth and may exceed bulk-junction limits). Verify by re-sim. |

### Long verticals → use a layer that doesn't share with horizontal power straps

A long met2 vertical traversing the full cell height **will collide
with horizontal met2 power straps at the same layer**. The strap is
a different net; you're shorting two power rails together.

Fix: **run the long section on met1**, transition to met2 only at
the endpoints where the vertical connects to the strap or another
met2 feature. Met1 over met2 is a normal cross-layer crossing and is
DRC-silent.

### Use `internal=True` for parent labels that shouldn't reach GDS

The `kind` annotation on a label (`NetName` / `PortName` /
`DeviceTerminal`) controls how net-level consumers treat it in the
F# viz tools and the .rkt sidecar — NOT whether Magic sees it as a
subckt port. `kind` does not survive the GDS export; Magic treats
every label on a port layer (`met1_label`, `li1_label`, etc.) as a
candidate port regardless of kind.

If you want a label to appear in viz / ratlines / the sidecar but
NOT in the GDS Magic reads (so it doesn't become a spurious port at
LVS), use the `internal=True` flag on the `Label`:

```python
# Net-name label for an internal-only net — visible in viz, absent
# from the GDS, so LVS doesn't see a phantom port at the parent.
rkt.Label(
    layer=rkt.named("sky130", "met1_label"),
    text="form_active_1v8",
    origin=(27923, -1170),
    internal=True,
)
```

In `.rkt` text the same thing reads:

```scheme
(label (layer sky130:met1_label) (text "form_active_1v8")
       (origin 27923 -1170) (internal #t))
```

**Practical rule**: at the parent level of an LVS-checked block, any
internal-only net label needs `internal=True`. Without it, the label
flows through to GDS and Magic promotes the net to a subckt port —
showing up as "Top level cell failed pin matching" with extra ports
on the layout side. Magic auto-traces internal connectivity from
geometry, so the internal label exists for your benefit (viz / nets
panel / `diagnose_met2_overlaps.py`'s net-inference), not for LVS.

Labels inside sub-blocks need the same treatment if they're internal
to that sub-block — same `internal=True` flag.

### Schematic must declare `nf` for multifinger devices

When the layout uses a wrapper subckt with `nf=N` internally, the
extracted netlist's effective `w` = per-finger `w` × N. The
schematic must match either by:

- Declaring `nf=N` *and* using the total effective width: `w=250 l=0.5 nf=10`.
- Or expanding into N parallel single-finger instances.

Mismatch shows up as an LVS property error (e.g., "circuit1: 250
vs. circuit2: 25, delta=164 %"). Structurally the circuits still
match; the property error gates an otherwise-clean LVS.

### Always DRC each primitive standalone after a generator change

Even a no-op-looking generator change (e.g., re-minting for a new
metadata version) can shift geometry slightly — and PDK draw procs
sometimes emit under-area features that only show up on specific
device sizes. **DRC every primitive standalone**, then **DRC every
wrapper that imports them**, after any generator change.

The Phase A run hit `met1.6` violations on `pfet_01v8_W2p0_L0p15` and
`nfet_01v8_W1p0_L0p15` only after the generator was rebumped for
DEVICE_TERMINAL tagging; the underlying gate-pad geometry was the
same as before. The `_fix_met1_min_area` post-processing pass in
`fet.py` was added in response.

---

## Hard rules

1. **Never reimplement PDK device geometry in Python or F#.** Always
   call the PDK's TCL draw proc through the `_magic_runner`. If a
   generator doesn't exist for the device you need, write one that
   shells out — don't draw the polygons by hand.

2. **Never hand-edit a primitive `.rkt`'s geometry.** The `(meta …)`
   block marks the cell as PDK-owned. Your edits get overwritten the
   next time the generator runs, and your changes won't survive a PDK
   version bump.

3. **Never hand-write a `.mag` file for new work.** Use the `.rkt`
   workflow. The viz tool's Magic CIF loader is there to handle
   legacy `.mag` files we already have, not as an authoring path.

4. **Never bypass the cache.** Always go through `gen_*` functions,
   never call `_magic_runner.run_magic` directly from caller code.
   The cache + provenance system is what makes the workflow
   deterministic.

5. **Never put non-PDK metadata in `(meta …)`.** That block is for
   generator provenance only. Cell description, owner, notes go in a
   cell-level `(props …)` element.

6. **Never ship a block without labels on its rails and signal
   nets.** Primitives inherit `D`/`G`/`S`/`B` device labels — those
   are NOT your net names. Power rails painted at the parent level
   are unnamed unless you `(label …)` them. See **Naming nets**
   above before declaring the block done.

7. **Never place `_core` primitives with arbitrary gaps.** Use
   `rekolektion.layout.place_row` (abut) or `place_tub` (parent
   well). The helpers refuse mixed well-types and compute origins
   from each primitive's bbox so wells either merge (Pattern A) or
   sit inside a shared tub (Pattern B). Hand-computing SRef origins
   with a "spacing" constant lands you in the `nwell.2a`
   no-man's-land — silent until DRC runs, then hundreds of
   violations cascade.

8. **Never ship a block without well taps.** `_core` primitives
   don't contain substrate-tap contacts. Use
   `place_taps_around(inner_bbox, well_type)` to add tap bands
   around your active region — `'pwell'` taps (psdm + tap) under
   nfet arrays, `'nwell'` taps (nsdm + tap) inside pfet tubs. Tie
   the resulting li1 strap to VSS / VDD with
   `place_rail(bbox, label, stitch_li1_straps=tap.li1_straps)`.
   Without taps the block fails `tap.5` and is latch-up vulnerable;
   without the stitch the well floats from the supply and LVS
   sees split nets.

9. **Never route everything on one metal layer, and never via
   directly onto an unpatched cell pin.** Use `pin_patch` at every
   cell-pin endpoint, `place_wire` on the preferred-direction
   layer (`met1` horizontal, `met2` vertical, `met3` horizontal),
   and `place_via` for layer transitions. Picking the wrong layer
   produces hairballs and crossing failures; skipping pin_patch
   fails via1 enclosure on every cell pin.

10. **Never declare a block "done" without running BOTH `verify_drc`
    AND `verify_lvs` — AND announcing the gate-3 pass as a headline.**
    `viz read` confirms geometry exists; `verify_drc` confirms it's
    manufacturable; `verify_lvs` confirms the electrical net graph
    matches the reference schematic.  DRC is necessary but NOT
    sufficient — a block can be DRC-clean and still have disjoint
    same-named net islands, mis-landed via stacks, or wrong-bulk taps
    that LVS will catch.  Run `verify_drc` first (cheaper, fails
    earlier on geometry problems), then `verify_lvs` against the
    reference SPICE.  Both are required before committing, AND the
    next user-facing line after `verify_lvs` returns `LVS MATCH`
    must be the headline "**LVS MATCH — gate 3 passed.**" — not
    buried in a paragraph about dimensions or routing.  See **Step 3
    — LVS** § "Announce the gate-3 pass explicitly" for the format.

11. **Never route internal nets in arbitrary order, and never
    treat phase boundaries as user check-ins.** Sort by topology
    phase before you start: power (1) → local-abut (2) →
    cross-row 2-pin (3) → multi-fanout (4). Within a phase,
    shortest-span first. See **Routing order** above. Routing
    `OUT (4-way)` before the simpler nets eats the channel that
    `drn_R (2-way)` would have used, and you end up jogging every
    easy net around the hard one. Once you start, walk all four
    phases in one pass — `verify_drc` at each boundary, but do
    NOT stop to ask "should I continue?" between them. Only halt
    on a DRC violation that needs a real architectural decision
    (re-place, re-layer, or new helper).

12. **Never start routing without a placement-review gate with
    the user.** After cells are placed and rails/taps are in, but
    before any `pin_to_rail` / `pin_patch` / `place_wire` call,
    stop.  Open the block in viz, describe the placement in text
    (dimensions, cell positions, alignment / aspect quirks), and
    ask the user: "placement OK or want changes?"  Routing begins
    only on approval.  Skipping this gate is the most expensive
    mistake in the workflow — every redirect after wiring starts
    burns N turns of unwinding.  Editing an existing block to
    fix a known bug is exempt; new layout-from-scratch is not.
    See **Placement review** above for the full procedure.

13. **Bridge in-cluster pins on parent metal before dropping to a
    trunk — never drop per-pin when a net touches multiple pins in
    one cluster.** A "cluster" is a contiguous routing region: a
    multi-finger primitive cell (`m>1`), the CS column(s) of one
    DAC bit, a row of identical instances, etc.  When a net's
    pin-set falls inside one cluster, paint **one parent met1
    horizontal bar inside the cluster** that shorts the pin li1
    strips (with mcons over each), then drop **one** met2 vertical
    from the trunk to the bar.  Per-pin drops stack via1+via2
    widening polygons 0–400 nm apart and cascade into mcon.2 /
    via.5a / met1.2 violations — DRC fails by the hundreds.
    Symptom in viz: multiple highlighted met2 verticals on the same
    net dropping from one trunk to adjacent cell pins.

14. **Plan drop-X uniqueness before painting met2 verticals — no
    two distinct nets may drop at the same X (or closer than the
    via2-widening pitch of ~510 nm).** Met2 verticals at the same
    X merge into one polygon; at <510 nm pitch the via2-widening
    rects at the trunk landing collide on met2.2 spacing.  Before
    starting Phase 4, enumerate the natural drop X of every
    multi-fanout net at every pin, sort by X, and check that any
    two adjacent drops belong to the **same** net (merge OK) or
    are ≥510 nm apart (different nets).  When two different nets
    share a natural drop X (e.g. a 1-col bit's `MAG.G`, `DIR_L.G`,
    `DIR_R.G` all sit at the cell-center X), insert a **met1
    horizontal stub at the pin's Y** from the pin to a free X
    slot, then drop from the slot.  Allocate per-cluster slot rows
    at ≥600 nm pitch; if slot count exceeds what the cluster
    width allows, widen BIT_GAP (or equivalent) before routing —
    don't try to fit them into the no-man's-land.
    Triage with `scripts/diagnose_met2_overlaps.py` (see
    "Diagnosing met2 overlap conflicts before DRC") — it groups
    by net-pair so different-net collisions surface even when
    the raw `met2.2` tile count looks small.

15. **Place each trunk near its pin-set, and trim it to its
    outermost knuckles — don't park every trunk at the top of the
    block and don't extend past the last drop.** Two halves:

    (a) **Trunk-Y follows pin-set.** When a bus's drop endpoints
    cluster in the south half of the cell band (e.g. CS gates
    sitting near the rail), the trunk belongs in the **south
    routing channel** just above the rail/tap row — drops go a
    short distance UP to the trunk.  When the pin-set lives in
    the north half (e.g. DIR drains at the top of stacked bit
    columns), the trunk goes in the **north routing channel**
    above the cell cluster — drops go DOWN.  Parking every trunk
    at one end forces half the nets into a "way up north and
    back down" round-trip, wastes met2, and crowds the routing
    channel with overlapping verticals that wouldn't conflict if
    each had its own short drop.

    (b) **Trim trunk x to the outermost drop.**  The trunk's
    `x1` / `x2` must end at the leftmost / rightmost drop X
    (plus `VIA_OVERSHOOT` for the via2 widening enclosure).  Do
    not paint a trunk that spans the entire block width when the
    drops only live in a sub-span.  Excess trunk metal east of
    the last knuckle picks up no signal, just consumes area and
    can land in a met3-spacing conflict with neighboring trunks
    at the same y-level.  Compute the drop x-range AFTER all
    drop X positions are finalized (post Rule #14), then paint
    the trunk to fit.

16. **Match wire width to via-pad width at every endpoint — never
    a step corner.**  `place_via` paints a met2 (or met3) pad sized
    to satisfy the via2.4/via3.4 alt-enclosure rule (≥0.085 µm on
    two opposite sides) — that's a **320 nm** pad around a 150 nm
    via1 cut, **370 nm** around a 200 nm via2.  If you paint the
    connecting wire NARROWER than the pad (e.g. `width_um=0.3` =
    300 nm into a 320 nm pad), the pad sticks out 10 nm on each
    side of the wire at the junction, producing an ugly **stair-
    step corner** in viz — the wire and pad merge on the metal
    layer, but the silhouette has unwanted notches.

    The same notch appears at every L-bend if `place_wire`'s chain
    form generates two segments at different widths, or if the
    wire passes by a via stack whose pad is wider than the wire.

    **How to apply.**  Decide a single canonical wire width per
    power/signal class — typically `0.32` µm for via1-pad-matched
    met2 wires, `0.37` µm for via2-pad-matched met3 wires.  Pass
    that exact width to every `place_wire` for the net.  When you
    need a different width somewhere (e.g. a wider trunk), match
    the trunk width to the WIDEST via pad it touches, then size
    branch wires to match.  Don't accept the default
    `MET2_MIN_WIDTH` (140 nm) for power rails — it's smaller than
    every via1 pad and guarantees stepped junctions.

    Visual check: at every via stack and every L-bend, the wire
    edge should be flush with the pad edge.  Any visible
    overhang or notch in viz means width mismatch — fix at the
    helper call, not by widening the pad locally.

17. **Never view your own .rkt work through GDS, and never review
    placement via static `viz-render` PNGs.** `viz_open` (MCP) and
    `viz app` (CLI) take `.rkt` directly via the LayoutLoader, which
    walks the `(import ...)` graph and renders the composed silicon-
    truth view. The `to-gds → open .gds` pipeline is for DRC / LVS /
    tape-out only — using it for review hides every post-edit change
    behind a stale snapshot (the GDS is a frozen artifact; the `.rkt`
    is the live truth). Same trap with `viz-render`: a one-shot PNG
    freezes one default layer set at one zoom and is useless for the
    iterative back-and-forth that placement review needs. The live
    viz GUI (or MCP `rekolektion_viz_open` + `rekolektion_viz_screenshot`)
    is the canonical placement-review surface for in-flight work; GDS
    and PNGs are downstream artifacts, not authoring surfaces. The
    only legitimate reasons to open a `.gds` are: (a) inspecting a
    tape-out merge result, (b) inspecting vendor IP that ships as
    GDS. For your own `.rkt` in-flight, always pass the `.rkt` path.

18. **When opening a file in any external viewer (viz, browser, GUI),
    the user-facing message MUST name the absolute path of the file
    being shown.** `viz_open` returns silently (just `{"ok": true}`)
    and the user cannot tell from the chat which file is now active —
    especially when multiple files are tabbed or when an earlier file
    is still focused. "Opened in viz", "loaded in browser", "GUI
    launched" without a path forces the user to guess. Format:
    `"Opened /abs/path/to/<file>.rkt in viz."` — single sentence, path
    verbatim. Applies to every `viz_open`, every
    `dotnet run -- app`, every browser launch, every screenshot fetch
    where the source file matters. The same rule extends to "saved a
    PNG / log / dump to" announcements — always quote the absolute
    path so the user can open it without searching.

19. **Compose `reflect` and `rot` via the documented matrix;
    NEVER treat `reflect=True` as X-flip.** The rkt format follows
    the GDS convention: `(reflect true)` mirrors the cell across
    the X-axis (Y-flip), while keeping x unchanged. For an X-flip
    (S/D pin x-swap with gate position preserved at top — the
    common cascode-layout need), use `reflect=True + rot=180.0°`
    together. The four combinations of `reflect × rot ∈ {0, 180}`
    each do something different; see **SRef transforms** for the
    matrix and parent-pin-coord computation. Mis-interpreting
    reflect as X-flip is a silent layout bug class: DRC passes,
    viz renders the cell visually correctly, and netgen masks the
    structural mismatch via BSIM4 S/D symmetry — "Netlists match
    uniquely with port errors" combined with disconnected-node
    reports for ports on transformed SRefs is the signature. Run
    **flat-extract LVS** (see *Step 3 — LVS* §"Flat-extract LVS")
    as the gold-standard verification for any cell with
    reflected SRefs.

20. **Match the FET's gate-contact variant to the body-tap side —
    `_topgate` (`botc=False`) for south taps, `_botgate`
    (`topc=False`) for north taps. NEVER body-tap a default
    (both-contact) variant on the close side.** The primitive's
    bottom gate li1 stamps and the body tap's li1 strap occupy
    overlapping y-ranges on the same plane — they merge into one
    polygon and silently tie gate to body in the extracted netlist.
    DRC waivers around the primitive footprint typically hide the
    underlying `li.3` violation; the only signal is an LVS
    "gate↔body merge through autonamed parent metal" symptom that
    looks like a Magic hierarchical extract bug but is actually
    real geometric overlap.

    Concrete trigger: `pfet_hv_W25p0_L0p5_nf10_core` default variant
    + `place_taps_around(sides=("bottom",))` → bottom gate li1
    at y=-12905..-12735 overlaps tap li1 strap at y=-13175..-12845
    by 60 nm. Found 2026-05-29 burning a half-day on bl_swap_mux LVS.

    For body-tied-to-source PMOSes (Phase A srcmux, current
    mirrors with body=VDD=S) the trap is silent and the netlist
    happens to come out right by coincidence — but the FET is
    still drawing wrong silicon. **Mint correctly anyway.** The
    `inspect_primitive` helper handles either variant; the
    body-tap inner_bbox should be read from the actual primitive's
    diff bbox (the `_topgate` / `_botgate` variants are
    asymmetric — diff extends past the missing-contact side), not
    hardcoded.

    See the "_topgate/_botgate" boxed note in *Authoring a
    primitive* for the geometric detail.

    **Enforced in tooling.** `place_taps_around` accepts an
    `inside_srefs=[...]` kwarg; when provided, it inspects each
    FET's gate-contact state (from the primitive's cell-name
    suffix) AND applies the SRef's `rot`/`reflect` transform so
    the check operates in PARENT-coord sides — not cell-local.
    Raises `ValueError` with the fix recipe if the requested tap
    side would collide. Use it at every call site:

    ```python
    taps = place_taps_around(
        active_bbox, "nwell", sides=("bottom",),
        inside_srefs=[pmos_sref],   # Hard Rule #20
    )
    ```

    Rotation handling matters: a `_topgate` (gate on cell-local
    top) SRef'd with `rot=180.0` puts its gate stamp on the
    PARENT bottom — south-side tap would collide. The check sees
    the transformed side and fires; the same FET at `rot=-90.0`
    (gate on parent east) doesn't collide with a south tap and
    passes silently. `reflect=True` (Y-flip) swaps cell-local
    top/bottom stamps before rotation, per the SRef-transforms
    matrix. The kwarg is optional for backward compatibility, but
    every new call should include it.

---

## Quick smoke test

The full workflow runs in one command:

```bash
.venv/bin/python scripts/demo_primitive_workflow.py /tmp/rkt_demo
```

If that works (primitives minted, block composed, paths printed),
the toolchain is healthy and you can build on top.
