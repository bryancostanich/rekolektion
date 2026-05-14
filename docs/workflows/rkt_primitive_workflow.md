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
| `gen_nfet_hv(w_um, l_um, nf=1, m=1, guard=False)` | sky130 5 V HV nfet | `guard=True` adds a substrate guard ring; default is `_core` (shared-guard variant) |
| `gen_pfet_hv(w_um, l_um, nf=1, m=1, guard=False)` | sky130 5 V HV pfet | same params |

> **`_core` means "designed to abut or live in a parent tub."** It is
> *not* a "smaller" or "lighter" primitive — it's a primitive that has
> the per-cell guard ring stripped, with the assumption that you'll
> either abut multiple `_core` cells (their wells merge) or place
> them inside one big parent-painted nwell/psub region. If you place
> `_core` cells with a small gap between them, you will hit
> `nwell.2a` and a cascade of related DRC violations. Read **Placing
> `_core` primitives** in the next section before you write any
> SRefs.

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
periodic-tap (latch-up) rule and you need interspersed tap rows.

**Where tap bands go relative to the well:**

| Surrounded primitives | `well_type` | Where the tap band sits |
| --------------------- | ----------- | ----------------------- |
| nfets in psub (no tub) | `'pwell'`  | Anywhere outside the FETs — psub is the default substrate |
| pfets in a `place_tub` nwell | `'nwell'` | **Inside the nwell tub**. The nwell is what the taps contact; if they sit outside the tub, they have no well to contact |

When in doubt, place tap bands such that their `tap` rectangle is
**inside** the well rectangle they're tapping. `place_tub` paints
its nwell large enough that the surround-style tap bands at the top
and bottom of the pfet array still fall inside it (the tub's default
margin is 0.4 µm; tap band's default clearance is 0.3 µm).

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

## Naming nets — DON'T skip this step

A `.rkt` block that has SRefs and parent-paint geometry but **no
labels** is not done — even if it renders correctly. The viz tool's
net view, the ratline overlay, the LVS flow, and the sidecar JSON
all key off **labels** to figure out which polygons belong to which
electrical net. Without labels:

- The ratline view shows the FETs' inherited port labels (`D`, `G`,
  `S`, `B`) and treats every `G` as the same net — visually
  misleading.
- The power rails you painted at the top and bottom of the block
  have no name and don't appear as nets at all.
- LVS will fail port matching against the reference SPICE.

### The D/G/S/B gotcha

Every primitive minted by `gen_nfet_hv` / `gen_pfet_hv` carries four
port labels baked in by Magic's `mos_draw` (with `doports=1`):

| Label | Meaning             |
| ----- | ------------------- |
| `D`   | drain               |
| `G`   | gate                |
| `S`   | source              |
| `B`   | bulk / body / tap   |

These are **device-terminal labels**, not net names. They're useful
inside the primitive (the LVS extractor uses them to identify the
FET's terminals), but at the block level they're meaningless —
every SRef'd nfet has its own `G` label, but those gates are NOT
all the same net. They're four different nets that happen to share
a string.

**Rule:** never use `D`/`G`/`S`/`B` as net names in your block.
Always override them by placing a parent-level `(label …)` with the
real signal name on top of the routing wire that connects to that
terminal.

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

### Declaring net semantics (optional but recommended)

The document-level `(nets …)` block lets you declare a net's
domain (`power`, `ground`, `signal`, `clock`, `analog`) and
voltage. This information is what the viz tool uses to classify
rails as power for the ratline view's `IsPower` flag, what LVS
uses for power-net checks, and what the SoC integrator reads to
hook up your block.

```scheme
(layout (version 1)
  (pdk sky130)
  (units (dbu_nm 1) (uu_um 1))
  …
  (nets
    (net VDD (domain power)  (voltage 5.0))
    (net VSS (domain ground))
    (net IN_P    (domain signal))
    (net IN_N    (domain signal))
    (net OUT     (domain signal))
    (net VBIAS_P (domain analog)))
  (cell my_block …))
```

`(nets …)` is optional — the viz tool's `isLikelyPowerNet`
heuristic catches the common cases by name pattern — but adding it
makes the block self-documenting and removes guesswork.

### Sanity check

After labeling, re-run `viz read` and confirm the cells loaded.
Then open the block in `app`, switch to the Nets tab, and check
that:

1. Power rails show up as named nets (`VDD`, `VSS`, …).
2. Each signal you intended exists with the expected pin count.
3. No FETs have a gate showing up only as `G` — every gate should
   either connect to a named signal or be flagged as floating in
   the inspector.

If any of these fail, the labeling is incomplete. Fix and re-check
before declaring the block done.

## Verifying your block

The viz CLI exposes five verbs; the two you'll use most are `read`
(fast summary, no GUI) and `app` (interactive GUI):

| Verb         | What it does                                                   |
| ------------ | -------------------------------------------------------------- |
| `read`       | Text summary: cell count, poly/path/sref totals, per-cell bbox |
| `app`        | Launch interactive 2D + 3D Avalonia viewer                     |
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

---

## Where files live

| Path | What |
| ---- | ---- |
| `src/rekolektion/primitives/sky130/` | Generator Python (fet.py, ...) |
| `src/rekolektion/layout/placement.py` | `place_row`, `place_tub` helpers |
| `src/rekolektion/layout/taps.py` | `place_taps_around` |
| `src/rekolektion/layout/rail.py` | `place_rail` (rail + auto-stitch) |
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

---

## Quick smoke test

The full workflow runs in one command:

```bash
.venv/bin/python scripts/demo_primitive_workflow.py /tmp/rkt_demo
```

If that works (primitives minted, block composed, paths printed),
the toolchain is healthy and you can build on top.
