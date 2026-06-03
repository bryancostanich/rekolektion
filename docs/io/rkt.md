# `.rkt` — canonical layout format

`.rkt` ("rekt") is rekolektion's text-based, comment-preserving,
PDK-aware layout format. Generators emit `.rkt` directly; the viz
tool reads/writes it; both GDS and Magic `.mag` remain as
interchange formats at the edges.

This doc is the reference. The design rationale lives in
[`docs/plans/canonical_layout_format.md`](../plans/canonical_layout_format.md);
the implementation decisions in
[`docs/plans/canonical_layout_format_decisions.md`](../plans/canonical_layout_format_decisions.md).

## Why a new format

GDS leaks: it has no port semantics, no named layers (just
number/datatype pairs), no comment channel. `.mag` is closer but
brings its own pain (polygon-decomposition into rectangles,
hierarchical port-promotion bugs documented in the rekolektion repo).
Neither is suitable as the canonical in-memory + on-disk model for
a generator-driven flow.

`.rkt` keeps the geometry and adds:

- **PDK-qualified layer names** (`sky130:met1`) instead of bare
  number pairs. Unknown layers stay visible as `unknown:N/D`.
- **First-class ports** with direction + flag set, attached to
  geometry.
- **Comments** preserved through edit, including AI-generated
  reasoning traces.
- **Imports** so multi-file projects compose without inlining.
- **Text format** that diffs cleanly in git.

## Five-line example

```scheme
(layout (version 1) (pdk sky130)
  (units (dbu_nm 1) (uu_um 1))
  (cell bitcell
    (poly (layer sky130:met1) (points (0 0) (100 0) (100 50) (0 50)))))
```

## Schema reference

Every `.rkt` file is a single top-level `(layout ...)` form. Inside
it, the children are header fields and content forms in roughly the
order shown:

```scheme
(layout
  (version 1)                    ; required, integer, currently 1
  (pdk sky130)                   ; default PDK for unqualified layer refs
  (units (dbu_nm 1) (uu_um 1))   ; integer scale; 1 nm/DBU is the SKY130 default
  (import "../primitives/fets.rkt")  ; optional, repeatable
  (top cell-name)                ; optional; otherwise the first cell is top
  (guides                        ; optional; editor alignment marks (see §Guides)
    (h 12500)                    ; horizontal guide at Y = 12500 DBU
    (v -800))                    ; vertical guide at X = -800 DBU
  (nets                          ; optional top-level net declarations
    (net BL (domain signal))
    (net VPWR (domain power) (voltage 1.8)))
  (cell <name> <elements>...))    ; one or more
```

### Cells

```scheme
(cell <name>
  (meta ...)?    ; optional, at most one — PDK-generated cells only
  <element>
  <element>
  ...)
```

Cell names are bare symbols. Elements inside a cell are any of the
forms below, in any order. The element's position is meaningful for
hit-testing index identity (the writer preserves order).

#### `(meta ...)` — generator provenance for PDK-minted cells

```scheme
(cell nfet_hv_W1p2_L1p0_core
  (meta
    (generator "sky130/nfet_hv")
    (params
      (w     1.2)
      (l     1.0)
      (guard 0)
      (mode  "lvt"))
    (source    "magic-cif sky130B 8.3 r638")   ; optional
    (generated "2026-05-13")                    ; optional, ISO 8601
    (digest    "sha256:9e3a1c…"))               ; optional
  (rect (layer sky130:diff) (-1055 -1625) (1055 1625))
  …)
```

Only `(generator …)` is required. `(params)` is always emitted (even
empty) so consumers distinguish "no params" from "schema malformed."

What `(meta …)` implies for consumers:

| Consumer        | Behavior when `(meta …)` present                                                              |
| --------------- | --------------------------------------------------------------------------------------------- |
| Viz editor      | Cell is read-only. Clicks inside select the parent `sref`. Inspector exposes "Regenerate…". |
| Tape-out (GDS)  | Ignored. Geometry alone determines output.                                                    |
| Cache lookup    | Key is `(generator, digest)`. Hit returns existing file; miss re-mints via the generator.    |
| Round-trip      | Reader populates `Meta`; writer emits verbatim. Non-interior edits preserve `Meta` bit-exact. |

Param values use the same lexer as `(props ...)`: bare integers, decimals,
quoted strings, or bare symbols (treated as atoms). Unknown sub-forms
inside `(meta ...)` are dropped — the schema is additive.

**Unknown-generator policy:** if `(generator "foo/bar")` doesn't match
a registered generator on the loading machine, the file loads
read-only. Geometry renders, tape-out works, "Regenerate" is disabled
and the inspector surfaces a hint. The cell is not an error.

**Anti-patterns:**

- Don't hand-edit geometry inside a cell that has `(meta …)`. The next
  regenerate overwrites it. If you need a tweak, fork params or copy
  the cell to a hand-authored variant without `(meta …)`.
- Don't use `(meta …)` for description / owner / notes. That's what
  a cell-level `(props …)` element is for.
- Don't trust `digest` for security. It's a cache key, not a tamper seal.

### Geometry elements

#### `(poly ...)` — closed polygon

```scheme
(poly (layer sky130:met1)
      (points (0 0) (100 0) (100 50) (0 50))
      (net BL)                      ; optional
      (props (note "BL stripe")))   ; optional
```

The point list is the polygon's vertices. If the last point doesn't
equal the first, the reader closes the polygon implicitly. The
canonical writer emits an explicit closing point.

#### `(path ...)` — centerline + width

```scheme
(path (layer sky130:li1)
      (width 170)
      (points (0 0) (500 0) (500 200))
      (cap round)                  ; optional: butt | round | square
      (net Q))
```

#### `(rect ...)` — axis-aligned rectangle

```scheme
(rect (layer sky130:met1) 0 0 100 50
      (net BL))
```

Sugar for a 4-point polygon. The four bare integers are
`x1 y1 x2 y2` and may appear in any order (the loader normalizes).

#### `(label ...)` — text annotation

```scheme
(label (layer sky130:met1)
       (text "BL")
       (origin 10 25)
       (class signal))   ; optional
```

Labels are points placed somewhere on the geometry they annotate.
Net-derivation walks labels first, then floods to connected
polygons on the same layer.

### Ports — first-class pins

```scheme
(port (name BL) (dir input)
      (layer sky130:met1)
      (flags signal scan)            ; optional, multiple allowed
      (shape (rect 0 0 10 50))       ; or (shape (poly (0 0) (10 0) (10 50) (0 50)))
      (net BL))                       ; optional, links to (nets ...) declaration
```

Direction: `input | output | inout | unspecified`.
Flags: any of `signal | power | ground | clock | analog | scan`.

### Hierarchy — `(sref ...)` and `(aref ...)`

```scheme
(sref (cell bitcell)
      (origin 100 200)
      (rot 90.0)        ; optional, default 0; CCW degrees
      (mag 1.0)         ; optional, default 1
      (reflect true))   ; optional, default false; reflects about X first
```

```scheme
(aref (cell wl_driver)
      (origin 0 0)
      (cols 64) (rows 1)
      (col_pitch 10 0)
      (row_pitch 0 5))
```

`sref` / `aref` reference cells by name. Resolution happens within
the current file first, then through any `(import ...)` forms.

### Layer references

`<pdk>:<name>` is the form. The reader resolves a bare `<name>`
(no colon) against the file's `(pdk ...)` header. `unknown:<n>/<d>`
is the escape hatch for layer-map misses — the reader keeps them
visible instead of dropping the geometry.

### Comments are first-class

```scheme
; provenance: generated by sram_assembler 2026-05-13
(layout (version 1) (pdk sky130)
  ; bitcell core, foundry-shape
  (cell bitcell
    ; metal-1 bitline contact — pitched 0.42 µm
    (poly (layer sky130:met1) (points (0 0) (100 0) (100 50) (0 50)))))
```

Comment lines (`; ...`) attach to the next form they precede. The
attachment survives editing: changing the polygon's points leaves
its leading comment alone. New forms emitted from code default to
no comments; populate the field explicitly when authoring.

The intended use is **provenance**: why a number is what it is, what
generated the form, the design constraint behind a choice. For AI
generators, comments are how the reasoning trace survives into the
file the next pipeline stage reads.

### Imports

```scheme
(layout (version 1) (pdk sky130)
  (import "../primitives/fets.rkt")
  (import "bitcell.rkt")
  (cell macro
    (sref (cell nfet_hv_W1p0_L1p0_core) (origin 0 0))
    (sref (cell bitcell) (origin 100 0))))
```

Path resolution is relative to the importing file. The loader walks
the import graph, detects cycles, and merges every loaded file's
cells into one in-memory document. The viz tool's Save preserves
each cell's source path — edits to a cell defined in an imported
file write back to that file, not the parent.

`(import ...)` is the right tool for multi-file projects. Do not
embed paths in `(sref (cell <path>))`; cell references are by name
only.

### Guides

Editor alignment marks — the dashed cyan lines the viz tool's
ruler-drag feature creates. Persisted in the `.rkt` so a layout
under review keeps the reviewer's measurement marks across save
+ reopen.

```scheme
(layout (version 1) (pdk sky130)
  (top blc_comparator)
  (guides
    (h 12500)         ; horizontal guide at Y = 12500 DBU
    (v -800)          ; vertical guide at X = -800 DBU
    (h 0))            ; horizontal at the origin
  (cell blc_comparator ...))
```

**Form.** A single optional `(guides ...)` block, written between
`(top ...)` and the first `(cell ...)`. Each child is either
`(h <int>)` (horizontal guide — constant Y in world DBU) or
`(v <int>)` (vertical — constant X). Order within the block is
preserved on round-trip; the position is the guide's stable
identity for the in-memory editor session.

**Coordinates** are world DBU at the **document level**, NOT
relative to any cell — guides span the whole viewport regardless
of where the camera is pointed. Negative integers are fine for
guides in the negative quadrant. Snap rounding is the user's
responsibility at drag-time; the writer emits whatever the
caller provides.

**Empty list.** A doc with no guides omits the `(guides ...)`
form entirely — files that don't use the feature round-trip
byte-for-byte. The reader treats a missing form as `[]` and
silently skips unknown children inside the block so future
schema growth (e.g. labeled or coloured guides) doesn't break
existing files.

**Multi-file routing.** Guides live on the **root** document only.
When the SaveRouter splits an edit across imported files, the
guide list flows back into the root file alone — imported subcell
files keep their own `Guides = []` so saving the root doesn't
re-touch every primitive.

**Viz integration.** The desktop tool's drag-from-ruler gesture
calls `GuidesService.commitDrag` on release, which dispatches a
`SyncGuidesToActiveDoc` Msg that copies the live set into the
active macro's `Document.Guides` and marks the macro dirty.
MCP-driven create / move / delete go through the same dispatch.
On document open and tab-switch, the service is replaced from
`Document.Guides` so the canvas overlay matches the file the
user is looking at.

**Tape-out.** Guides are editor metadata. `to-gds` ignores them;
the GDS export contains no equivalent record. If a downstream
consumer cares about alignment marks it should walk the `.rkt`
directly.

### Property bag

Every element accepts an optional `(props ...)` block for
free-form metadata that doesn't fit the schema:

```scheme
(props (drc_waiver "issue-#42")
       (origin_note "anchor at bitcell (0,0)")
       (count 7)
       (ratio 1.5))
```

Property values are bare symbols, quoted strings, integers, or
floats. The format doesn't validate property keys — they're a
generator/tool agreement.

#### Reserved cell-level conventions

A cell-level `(props ...)` element (a `(props ...)` direct child of
`(cell ...)`, not attached to a geometry element) holds metadata
about the cell as a whole. Certain keys are reserved by convention —
emitters and consumers should treat them consistently:

| Key | Value form | Meaning |
|---|---|---|
| `bbox` | `(x0 y0 x1 y1)` integers in DBU | **Declared cell extent** — the author/generator's definitive bounding box, equivalent to Magic's `FIXED_BBOX` or a foundry's `AREAID` marker. Set by the generator (or hand-author) at emit time; **never derived from a polygon bbox query**. LEF emitters use this directly for `SIZE`; viz tools draw a cell outline at this rectangle even when interior geometry is sparser. |
| `description` | quoted string | One-line human description of the cell (shown in the viz inspector). |
| `owner` | quoted string | Track / author tag for non-PDK cells. |
| `notes` | quoted string | Free-form longer notes (one prop entry per note). |

Example:

```scheme
(cell cim_reram_array_256x64
  (props (bbox -1140 -720 6432 720)
         (description "256×64 ReRAM CIM array — Track 09")
         (owner "track-09"))
  (sref (cell cim_reram_2t2r_b1) (origin 0 0))
  …
  (port (name BL[0]) (dir inout) (layer sky130:met1) (flags signal)
        (shape (rect -150 -500 150 -300)))
  …)
```

**Why `bbox` is a property, not a schema field:** the format stays
additive. Older readers ignore the unknown key and still load the
cell. New readers that need the declared extent (LEF emitters,
floorplan tools) read it from `(props …)`. The convention is
generator/tool agreement, not schema enforcement.

**Anti-patterns:**

- **Don't derive `bbox` from a polygon bbox query.** That's the
  foundry-LEF anti-pattern this convention exists to prevent —
  accidental geometry outside the intended cell extent (misplaced
  caps, dummy fill, guard rings) inflates the reported size.
- **Don't omit `bbox` for cells consumed by LEF emitters.** The
  emitter has no other authoritative source. Polygon bbox is a
  bug source.
- **`bbox` integers are DBU, not nanometres or microns.** Multiply
  by `Units.DbuNm` for nm; divide by `Units.UuUm` for µm. The
  format's storage layer is integer DBU everywhere.

## Python API

```python
from rekolektion.io import rkt

doc = rkt.Document(
    header_comments=["generated 2026-05-13 by my_generator"],
    cells=[
        rkt.Cell(
            name="bitcell",
            comments=["foundry-shape 6T cell, 1.31×1.58 µm"],
            elements=[
                rkt.Poly(
                    layer=rkt.named("sky130", "met1"),
                    points=[(0, 0), (100, 0), (100, 50), (0, 50)],
                    net="BL",
                    comments=["metal-1 bitline stripe"],
                ),
                rkt.Port(
                    name="BL",
                    direction=rkt.PortDirection.INPUT,
                    layer=rkt.named("sky130", "met1"),
                    flags=[rkt.PortFlag.SIGNAL],
                    shape=rkt.RectShape(0, 0, 10, 50),
                ),
            ],
        ),
    ],
    top_cell="bitcell",
)
open("bitcell.rkt", "w").write(rkt.write(doc))
```

Every schema form has a Python dataclass:
`Document`, `Cell`, `Net`, `Import`, `Units`, plus element variants
`Poly`, `Path`, `Rect`, `Port`, `Label`, `SRef`, `ARef`, `Props`.
Layers use `rkt.named(pdk, name)` or `rkt.unknown(number, datatype)`.
Property values are `str` (quoted), `int`, `float`, or `rkt.Symbol`
(unquoted symbolic value).

The writer is canonical — same input always produces the same
output bytes. Round-trips through the F# reader byte-for-byte.

## F# API (in-tree consumers)

```fsharp
open Rekolektion.Viz.Core.Rkt

// Parse
match Reader.parseFile "bitcell.rkt" with
| Error e -> printfn "parse error: %s" e.Message
| Ok (cst, doc) ->
    for cell in doc.Cells do
        printfn "%s — %d elements" cell.Name cell.Elements.Length

// Synthesise
let text = Writer.write doc
File.WriteAllText("out.rkt", text)

// Load with import resolution
match Reader.loadSingle "macro.rkt" with
| Error e -> ...
| Ok library ->
    // library.Documents : Map<string, LoadedDocument>
    // library.CellIndex  : Map<cellName, path>
    ()
```

Types live in `tools/viz/src/Rekolektion.Viz.Core/Rkt/Types.fs`.

### Conversion modules

The `Rkt/` namespace ships two downstream-format emitters plus a
reverse reader:

| Module | Purpose | Inputs the `.rkt` semantics it consumes |
|---|---|---|
| `Rkt.ToGds` | Tape-out GDS emit (`toLibrary doc → GdsLibrary`). | Geometry, hierarchy, layer table. Net annotations dropped per GDS limits. |
| `Rkt.ToLef` | LEF 5.7 abstract emit. CLI: `to-lef <input.rkt> <out.lef>`. | Cell-level `(props (bbox …))` → LEF `SIZE`. `(port …)` elements → LEF `PIN` entries (direction + flags → `USE`, shape → `RECT`). `ObsPolicy` (default `FullSize met1, met2`) controls obstruction emit. Errors on missing `bbox`, unknown layer, off-grid coord. |
| `Rkt.OfGds` | Reverse — GDS read into a `.rkt` document. Lossy for net metadata; geometry round-trips. | — |

`Rkt.ToLef` plan / mapping reference:
[`docs/plans/rkt_to_lef_emitter.md`](../plans/rkt_to_lef_emitter.md).

## Conventions

These are how rekolektion's own generators use the format. Follow
them when emitting `.rkt` from new code so files stay
interoperable.

| Convention | Why |
|---|---|
| **Always set `(pdk sky130)`** | Bare layer names resolve against this. |
| **Use named layers, not `unknown:N/D`**, when a SKY130 mapping exists. | Downstream tools display the name; unknown pairs render in a fallback theme color. |
| **Put generator provenance in `Document.header_comments`**. | First place a reader (human or AI) looks to understand the file's origin. |
| **One cell per `(cell ...)` form; use `(import …)` for cross-file references.** | The format's resolution model. Don't embed paths in `(sref ...)`. |
| **Comments before each `(cell ...)` describe what it is; comments before each element describe why that geometry exists.** | The two granularities the viz tool surfaces in its inspector. |
| **Integer DBU only.** | The format forbids floats at the storage layer; multiply by `Units.DbuNm` to get nanometers. |
| **For SRef/ARef rotations, emit `(rot ...)` only when non-zero, `(mag ...)` only when non-1, `(reflect ...)` only when true.** | The writer omits defaults; new files should match. |

## Anti-patterns

- **Don't paste paths into `(sref (cell ...))`.** Use `(import ...)`.
- **Don't strip comments on save.** Whoever generated the file
  encoded intent in them; downstream tools (and humans) read them.
- **Don't use `(props ...)` for things the schema already covers.**
  If you have a port, use `(port ...)`. Properties are for
  metadata that doesn't fit any schema field.
- **Don't omit `(version ...)`.** Readers may reject unversioned
  files in future versions.

## File extension + tooling

- Extension: `.rkt`. Reuses Racket's extension intentionally —
  editors with Racket support get S-expression syntax highlighting
  + paredit for free.
- MIME type: `text/plain`.
- F# parser/writer: `tools/viz/src/Rekolektion.Viz.Core/Rkt/`.
- Python writer: `src/rekolektion/io/rkt.py`.
- The viz tool reads `.rkt` via File → Open; saves via File → Save
  As with a `.rkt` extension.

## Open gaps (v1)

Each gap below has a feature-request plan filed under
`docs/plans/`. The plans carry the API surface, mapping tables,
acceptance criteria, and phased implementation.

- The Python writer has no reader yet. Python-side consumers that
  want to read `.rkt` go through the F# reader (or wait for the
  Python reader).
  → [`docs/plans/rkt_python_reader.md`](../plans/rkt_python_reader.md).
- Save-routing per imported file isn't tracked in the App yet —
  edits to a cell defined in an imported file currently write into
  the root file on save. The cell-origin metadata exists at the
  reader layer; the App's editor just doesn't consult it yet.
  → [`docs/plans/rkt_per_file_save_routing.md`](../plans/rkt_per_file_save_routing.md).
- Comments inside an element (between sub-forms like `(layer ...)`
  and `(points ...)`) are dropped on parse. Comments before the
  outer form survive.
  → [`docs/plans/rkt_in_element_comments.md`](../plans/rkt_in_element_comments.md).
