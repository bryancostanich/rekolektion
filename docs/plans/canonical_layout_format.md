# Canonical Layout Format — design proposal

Status: draft, pre-decision.

## Why this exists

The viz tool currently uses an in-memory model shaped like GDS: `(Layer,
DataType)` integer pairs, no port flags, no label classes, no PDK
provenance. That model leaks in three ways:

1. **Layer provenance.** Opening a random `.gds` and seeing `(94, 20)`
   tells us nothing about which PDK it came from. We assume SKY130
   project-wide; nothing in the file says so. Catalog misses (recent:
   `nsdm (94/20)`) silently drop polygons from the render.
2. **Port semantics.** GDS `TEXT` records are loose: no direction
   (input/output/inout), no flags (signal/power/ground/clock), no
   geometry association. We rely on out-of-band Magic post-processing
   to get usable ports for LVS and OpenROAD.
3. **Label classes / properties.** Both Magic and our verify flows
   carry richer metadata than GDS represents (Magic port flags, label
   sticky classes, net hints). Round-tripping through GDS strips it.

`.mag` solves (2) and (3) but introduces two new problems:
- **Polygon decomposition.** Magic's internal model is rectangles
  only. Foundry SRAM cells in rekolektion have angled geometry; .mag
  rectangulates it lossily.
- **Magic-outside-Magic fragility.** Documented in `CLAUDE.md`:
  hierarchical port promotion bugs (commits `b09c441`, `a97f56f`,
  tasks #36/44/64/103/105), aligner workarounds in
  `scripts/run_lvs_*.py`. Treating `.mag` as canonical means hosting
  these bugs in the canonical layer.

`.gds` and `.mag` are both fine as **interchange formats** — we still
need to read/write both. The question is what the **in-memory + on-disk
canonical** is, and the answer is: neither.

## Goals

- **PDK-qualified layer names** are the primary identifier, not
  (number, datatype) pairs.
- **Polygon-native geometry.** Angled geometry is first-class; no
  silent rectangulation.
- **Port records with explicit semantics.** Direction + flag set,
  attached to geometry.
- **Properties bag** on every element for extensibility without
  schema bumps.
- **Text-based, diff-friendly, comment-preserving** round-trip.
- **Lossless GDS import** (with `unknown:<n>/<dt>` for unmapped layers
  — visible, not dropped).
- **Lossless .mag import** for the parts of .mag that map cleanly;
  documented losses for the parts that don't (Magic-internal markers,
  Magic-only labels).

## Non-goals

- Replacing GDS or .mag as interchange formats. We export both.
- Carrying simulation results, schematic data, or netlists. Sidecar
  formats handle those.
- Being a foundry-submitted format. GDS stays the deliverable.

## Format: S-expressions

Decision factors covered in chat. Summary:

- Trivial parser (~50 lines per language).
- Dispatch on head symbol — no `type:` discriminator boilerplate per
  element.
- Order is preserved by the format, not by parser convention.
- Comment-preserving round-trip is straightforward.
- ~2-3× more compact than YAML for dense geometry.

YAML was the close runner-up. Rejected primarily because (a) `type:`
discriminators add ~20% line-count overhead with no semantic gain,
(b) the type-coercion footguns (Norway problem, octal-prefix ints) are
real risks for layer-number-shaped data, (c) F# YAML libraries that
preserve comments are absent; we'd write our own anyway.

File extension: `.rkt` (rekolektion / "rekt"). Reuses Racket's extension
intentionally — any editor with Racket support gets S-expr syntax
highlighting, paredit, and structural editing for free. The only
gotcha is `racket some-file.rkt` would try to execute it as a Racket
program; unlikely to be hit accidentally.

Mime type: text/plain.

## File composition

Two capabilities, both supported, independent:

1. **A `.rkt` file may contain one or more `(cell ...)` forms.**
2. **A `.rkt` file may `(import "path/to/other.rkt")`** to bring in
   cells defined elsewhere.

The combinations work as expected: a single self-contained file is
fine (everything inline, no imports), Magic-style cell-per-file is
fine (one cell + imports for everything else), and any mix in
between is fine.

Path resolution at v1: **relative to the importing file, full stop.**
No search paths, no `$PDK_ROOT`-rooted imports, no environment
variable substitution. Keeps the spec minimal; can extend later if
needed.

### Editor behavior

- **Open** follows imports and presents the full hierarchy.
- **Save** preserves the source structure: edits to a cell defined
  inline stay inline; edits to a cell defined in an imported file
  modify the imported file (since that's where the source of truth
  lives).
- **Save As / Flatten** is an explicit export option that inlines
  every imported cell into a single self-contained file. Not the
  default — the default is "preserve what you opened."

## Schema sketch

```scheme
(layout (version 1)
  (pdk sky130)
  (units (dbu_nm 1) (uu_um 1))

  (nets
    (net BL    (domain signal))
    (net VPWR  (domain power) (voltage 1.8))
    (net VPP   (domain power) (voltage 3.3) (class form-pulse)))

  (cell sram_top
    (poly (layer sky130:met1) (net BL)
          (points (0 0) (100 0) (100 50) (0 50)))

    (path (layer sky130:li1) (width 170)
          (points (0 0) (500 0) (500 200)))

    (port (name BL) (dir input) (layer sky130:met1)
          (flags signal)
          (shape (rect 0 0 10 50)))

    (label (layer sky130:met1) (text "BL") (origin 10 10))

    (sref (cell bitcell) (origin 0 0) (rot 0) (mag 1.0))

    (aref (cell wl_driver) (origin 0 200)
          (cols 64) (rows 1)
          (col_pitch 10 0) (row_pitch 0 5))

    (props (drc_waiver "issue-#42")
           (origin_note "anchor at bitcell (0,0)")))

  (cell bitcell ...))
```

### Element types

| Form | Purpose | Required fields | Optional fields |
| --- | --- | --- | --- |
| `(poly ...)` | Closed polygon (≥3 points) | `layer`, `points` | `net`, `props` |
| `(path ...)` | Centerline + width | `layer`, `width`, `points` | `net`, `cap`, `props` |
| `(rect ...)` | Axis-aligned rect (sugar for poly) | `layer`, four coords | `net`, `props` |
| `(port ...)` | Pin definition + flag set | `name`, `dir`, `layer`, `shape` | `flags`, `net`, `props` |
| `(label ...)` | Text annotation | `layer`, `text`, `origin` | `class`, `props` |
| `(sref ...)` | Single cell instance | `cell`, `origin` | `rot`, `mag`, `reflect`, `props` |
| `(aref ...)` | Cell array | `cell`, `origin`, `cols`, `rows`, `col_pitch`, `row_pitch` | `rot`, `mag`, `reflect`, `props` |
| `(props ...)` | Free-form key/value bag | none | any keys |

### Layer references

Always **PDK-qualified**: `<pdk>:<name>`. Examples: `sky130:met1`,
`sky130:nsdm`, `sky130:areaid.sc`. Unknown layers from GDS import are
written as `unknown:<number>/<datatype>` — visible in the viz tool,
flagged in the layer panel, and easy to grep for.

The `(pdk sky130)` header declares the default PDK for unqualified
names within a file (we still discourage unqualified usage, but it's
allowed for terseness in the common case).

### Port flags

Sum type subset:
- `signal` — ordinary data.
- `power` — VDD-class supply.
- `ground` — VSS-class supply.
- `clock` — clock domain root.
- `analog` — bias / reference; LVS treats as power-domain-equivalent.
- `scan` — scan chain participant.

Direction values: `input | output | inout | unspecified`.

Multiple flags allowed (a clock port also signaled as scan).

### Coordinates

Integer DBU throughout. `(units)` header declares the DBU scale; the
generator/viz tool both default to `dbu_nm: 1` (1 nm per DBU = SKY130
convention). Floating point is forbidden at the storage layer to
avoid round-trip drift.

### Hierarchy + ordering

- `(cell ...)` order in the file is preserved; the first cell is the
  default "top" unless `(top cell-name)` overrides.
- Element order within a cell is preserved (matters for fill order
  in some draw conventions).

### Versioning

`(version N)` is the first child of `(layout ...)`. Required even at
v1. Reader supports all versions ≤ its build-time max via in-tree
migration code. New schema additions are minor bumps (still v1) if
they're additive (ignorable by older readers); breaking changes
require a major bump and a migration pass.

## Import / export

**GDS import:**
- Layer numbers → names via PDK layer-map table. Unknown pairs become
  `unknown:N/D`.
- Polygons preserve point lists verbatim.
- Paths preserve width.
- SRefs/ARefs preserve transforms (`rot`, `mag`, `reflect`).
- TEXT records become `(label ...)` with no `class`. (Real ports
  require either .mag import or hand-annotation.)

**GDS export:**
- Names → numbers via the same map.
- `(unknown:N/D)` layers export verbatim.
- Polygons + paths preserved.
- `(port ...)` records emit as `(label ...)` PLUS a property carrying
  port flags (so the info isn't truly lost even though GDS can't
  represent it). A separate `.ports.json` sidecar would be the
  cleaner long-term answer.

**.mag import:**
- Magic layer names → PDK-qualified names.
- Magic `flags` field maps onto `(port ... (flags ...))`.
- Rectangles → `(rect ...)`.
- Hierarchy preserved.

**.mag export:**
- Polygon decomposition into rectangles (lossy; warn on non-Manhattan).
- Port flags preserved.

## Implementation

Order of work:

1. **F# parser/writer** in `tools/viz/src/Rekolektion.Viz.Core/Rkt/`
   (`Types.fs`, `Cst.fs`, `Reader.fs`, `Writer.fs`). Mirror the
   existing `Gds/Types.fs` and `Mag/Writer.fs` modules. CST type
   carries trivia (comments + whitespace) attached to adjacent
   nodes; AST is derived by walking the CST and dropping trivia.
   Writer walks the CST so untouched nodes round-trip byte-exact.
   Import resolution (`(import "...")`) is path-relative to the
   importing file, with cycle detection.
2. **In-memory model migration.** `Library` type either evolves to
   match the new schema (preferred) or a parallel `RktDocument` type
   is added with adapters. Decision deferred to implementation.
3. **GDS import wired through Rkl.** Loader produces an in-memory Rkl
   document; viz tool works on that.
4. **.mag import wired through Rkl.**
5. **Python writer** in `src/rekolektion/io/rkt.py`. Generator emits
   `.rkt` as primary, calls a thin `rkt_to_gds.py` for tapeout/DRC.
6. **viz Save path** writes `.rkt` for `.rkt`-origin files, `.gds`
   for `.gds`-origin, `.mag` for `.mag`-origin (existing
   format-of-origin policy preserved).

Tests at each step. Round-trip property tests are the key safety net:
`gds → rkt → gds` must be byte-equivalent modulo non-semantic
reordering; `mag → rkt → mag` likewise for the subset .mag can
represent.

## Open questions

- ~~Should `(net ...)` be an element-level field or its own block?~~
  **Decided: both.** Top-level `(nets ...)` block declares net
  properties (domain, voltage, class). Element-level `(net BL)` field
  is membership-by-name. Undeclared nets are allowed (default
  `signal` domain, no voltage spec).
- ~~Do we carry `flagstring` Magic compatibility?~~ **Decided: yes,
  as an opaque escape hatch.** Enum (`signal | power | ground |
  clock | analog | scan` × `input | output | inout | unspecified`)
  is the source of truth for semantics. On .mag import, original
  Magic flagstring is preserved in `(props (magic_flags "..."))`
  alongside the decoded enum. On .mag export the enum wins; the
  `magic_flags` property is used only when the port's flags field
  is `unspecified` AND the property is present (passthrough for
  .mag-origin files that were never edited at the port level).
- ~~Comment preservation.~~ **Decided: full preservation
  (CST-based).** Parser tokenizes comments as trivia and attaches
  them to adjacent nodes. Writer walks the CST and emits trivia
  inline. Round-trip is byte-exact for files that go in and out
  untouched; edits preserve untouched neighbors' comments.

  Rationale: comments are the **provenance channel for intent** —
  for human annotations (DRC waivers, design rationale, "this offset
  matches issue #42") AND for AI-generated layouts where the comments
  are the reasoning trace. Stripping comments on save would erase
  that channel every time the viz tool writes the file, blinding any
  downstream tool (human or AI) that reads it next. Real cost in F#
  is ~60-80 LOC extra over a strip-on-write parser, not the 200 I
  initially over-estimated.
- ~~Where do checked-in `.rkt` files live?~~ **Out of scope for the
  format spec.** Directory layout, derived-artifact policy, and
  migration sequencing are consumer-project decisions, not format
  design. The format doesn't depend on them.

## Decision

Approved 2026-05-12. All open questions resolved (see above).
Implementation proceeds per the order in **Implementation** section.
