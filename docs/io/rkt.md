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
  (top cell-name)                ; optional; otherwise the first cell is top
  (import "../primitives/fets.rkt")  ; optional, repeatable
  (nets                          ; optional top-level net declarations
    (net BL (domain signal))
    (net VPWR (domain power) (voltage 1.8)))
  (cell <name> <elements>...))    ; one or more
```

### Cells

```scheme
(cell <name>
  <element>
  <element>
  ...)
```

Cell names are bare symbols. Elements inside a cell are any of the
forms below, in any order. The element's position is meaningful for
hit-testing index identity (the writer preserves order).

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

- Comments inside an element (between sub-forms like `(layer ...)`
  and `(points ...)`) are dropped on parse. Comments before the
  outer form survive.
- The Python writer has no reader yet. Python-side consumers that
  want to read `.rkt` go through the F# reader (or wait for the
  Python reader).
- Save-routing per imported file isn't tracked in the App yet —
  edits to a cell defined in an imported file currently write into
  the root file on save. The cell-origin metadata exists at the
  reader layer; the App's editor just doesn't consult it yet.
