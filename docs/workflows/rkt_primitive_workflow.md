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

A block is a `.rkt` file under `cell_designs/<group>/<name>.rkt`. You
can author it three ways:

### Option A — programmatic (Python)

Best when the block is arrayed, parameterized, or has many instances.
Use `rekolektion.io.rkt`:

```python
from pathlib import Path
from rekolektion.io import rkt
from rekolektion.primitives.sky130 import gen_nfet_hv, gen_pfet_hv

nfet = gen_nfet_hv(w_um=1.2, l_um=1.0)
pfet = gen_pfet_hv(w_um=1.2, l_um=1.0)

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
                rkt.SRef(cell=nfet, origin=(0, 0)),
                rkt.SRef(cell=pfet, origin=(4000, 0)),       # 4 µm offset
                rkt.Rect(layer=rkt.named("sky130", "met1"),
                         x1=1055, y1=-150, x2=2945, y2=150),
            ],
        ),
    ],
    top_cell="my_block",
)

Path("cell_designs/my_group/my_block.rkt").write_text(rkt.write(doc))
```

Coordinates are in DBU (1 nm by default — see `(units (dbu_nm 1))`).
A 4 µm offset is `4_000` DBU, etc.

**Import-path rule of thumb:**

| Block lives at                            | Import path to a primitive  |
| ----------------------------------------- | --------------------------- |
| `cell_designs/<group>/<block>.rkt`        | `../primitives/<name>.rkt`  |
| `cell_designs/<block>.rkt` (rare)         | `primitives/<name>.rkt`     |
| `demo_output/<block>.rkt` (scratch space) | `primitives/<name>.rkt`     |

When in doubt, count directories from your block file up to the
`cell_designs/` root — that's how many `../` you need.

### Option B — hand-authored `.rkt`

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

### Option C — hybrid

Python emits the skeleton (imports + sref scaffolding), then a human
or agent tweaks placement, adds parent paint, fills in nets. The
`.rkt` round-trips through both the Python writer and the F# editor,
so it doesn't matter which side authored which lines.

---

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

---

## Quick smoke test

The full workflow runs in one command:

```bash
.venv/bin/python scripts/demo_primitive_workflow.py /tmp/rkt_demo
```

If that works (primitives minted, block composed, paths printed),
the toolchain is healthy and you can build on top.
