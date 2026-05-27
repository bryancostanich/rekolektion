# `Rkt.ToLef` — LEF emitter from `.rkt`

**Status: LANDED 2026-05-27.** Implementation at
`tools/viz/src/Rekolektion.Viz.Core/Rkt/ToLef.fs` (544 lines) with
unit tests at `tools/viz/tests/Rekolektion.Viz.Core.Tests/RktToLefTests.fs`
(478 lines). CLI: `to-lef <input.rkt> <out.lef>` (see
`tools/viz/src/Rekolektion.Viz.Cli/Program.fs` `cmdToLef`).
Documented in [`docs/io/rkt.md`](../io/rkt.md) under "Conversion
modules"; previously listed as a gap there, entry removed when the
implementation landed.

First consumer: khalkulo ReRAM_IRL Track 09 array assembly
(`khalkulo/conductor/projects/ReRAM_IRL/tracks/09_array_assembly/plan.md`).

The sections below capture the goals + mapping reference that
guided the implementation and remain the documentation of intent
for future maintenance — not a pre-implementation feature request.

## Why this exists

Rekolektion has two existing LEF emitters in Python, both of which
predate the `.rkt` schema:

- `src/rekolektion/macro/lef_generator.py` — v2 SRAM macro emitter.
  Computes pin positions from the assembler floorplan via formulae
  (`_PIN_LAYER`, `_PIN_STUB_LEN`, `_PIN_STUB_W`, …); see
  `lef_generator.py:generate_lef`.
- `src/rekolektion/macro/sub_lef.py` — per-sub-block LEF emitter for
  the OpenROAD macro flow. Same pattern.

Both share the **formulaic-pin anti-pattern** the `.rkt` schema was
designed to replace. The chain of inference is:

1. The macro is described by an in-memory `MacroParams` /
   floorplan struct, not by a `.rkt` document.
2. Pin coordinates and the macro `SIZE` are recomputed *from those
   parameters* at LEF-emit time.
3. Any drift between (a) the GDS the assembler emits and (b) the LEF
   the emitter writes is invisible — the two are computed
   independently from the same parameter struct, but neither reads
   the other.

The `.rkt` schema closed this gap on the **input side** in three
places:

- `(props (bbox …))` at the cell level — the **declared** cell
  extent, set by the generator at emit time, never derived from
  polygon-bbox queries (the anti-pattern called out in
  `rkt.md → "Reserved cell-level conventions"`).
- First-class `(port …)` elements with explicit `(dir …)`,
  `(layer …)`, `(flags …)`, and `(shape …)`.
- First-class `(label …)` elements for net annotation, with
  net-derivation that walks labels first and then floods to
  connected polygons on the same layer.

What was missing was the **output side**: a `Rkt.ToLef` module that
consumes `Document` / `Cell` and emits a LEF 5.7 abstract that uses
the declared `bbox` for `SIZE` and the declared `(port …)` elements
for `PIN` entries. No re-derivation from formulas. **Landed 2026-05-27
at `tools/viz/src/Rekolektion.Viz.Core/Rkt/ToLef.fs`** — the sections
below document the design intent that guided that implementation.

## Goals

- **Authoritative single source.** A cell's `.rkt` file is the input;
  the LEF abstract is a derived view. Re-running the emitter on the
  same `.rkt` always produces byte-identical LEF.
- **No polygon-bbox derivation for `SIZE`.** `SIZE` comes from
  `(props (bbox x0 y0 x1 y1))`. If a cell has no `bbox` prop, the
  emitter errors out with a clear message rather than silently
  falling back to a computed bbox.
- **Port-driven `PIN` entries.** Each `(port …)` becomes one LEF
  `PIN` entry. Direction, flags, layer, and shape all come from the
  port declaration — none of them computed.
- **PDK-layer-aware emit.** `(layer sky130:met1)` resolves to LEF
  layer name `met1` via the same SKY130 layer table used by
  `ToGds.fs` / `OfGds.fs`. `unknown:N/D` layers are an error (LEF
  has no concept of bare number/datatype pairs).
- **mfg-grid-snapped coordinates.** All emitted LEF coordinates land
  on the SKY130 5 nm manufacturing grid (the same `_snap` discipline
  the legacy `lef_generator.py` carries — see its `_snap(v, grid=0.005)`
  helper). Off-grid coordinates trigger `DRT-0416` errors during
  chip-level detailed routing.
- **`OBS` (obstruction) generation policy.** Layer-conservative obs
  rectangles derived from the union of the cell's drawn geometry on
  each obstruction-eligible layer (configurable per emit call;
  default = full-`SIZE` obs on met1+met2, matching the legacy v2 SRAM
  policy documented at `lef_generator.py` head).
- **Stable, diff-friendly output.** Same input always produces the
  same output bytes (same property ordering for `PIN`, same coordinate
  precision, same blank-line policy). Mirrors the `Writer.write` /
  `ToGds.toLibrary` discipline already in `Rkt/`.

## Non-goals

- **Replacing `lef_generator.py` / `sub_lef.py` in this milestone.**
  Both stay in place for the v1 SRAM macro flow until the assembler
  itself emits `.rkt` for the macro top cell. Concurrent emit
  (legacy formulaic + new `.rkt`-driven) for cross-validation is the
  recommended migration path; cutover is a separate plan.
- **Inverse direction (LEF → `.rkt`).** LEF is lossy compared to the
  full layout (no internal geometry, just abstracts). Not a fit for
  the `Rkt.Of*` family.
- **Liberty / SPICE / Verilog emit.** The same "consume `.rkt`,
  produce X" pattern applies, but each has its own data shape and
  belongs in its own plan.
- **LEF 5.8+ features.** `PROPERTY` blocks beyond the standard set,
  `FIXEDMASK`, `ANTENNA*` advanced attributes — out of scope for v1.
  Add as needed by consumers.

## Mapping reference

The mapping from `.rkt` to LEF is mostly direct because the `.rkt`
schema was designed around the LEF abstraction. The table below is
the authoritative mapping; deviations are bugs.

| `.rkt` form | LEF construct | Notes |
|---|---|---|
| `(cell <name> …)` | `MACRO <name> … END <name>` | One LEF macro per top-level cell. SRefs/ARefs inside the cell are not flattened — they're internal geometry, invisible to LEF. |
| `(props (bbox x0 y0 x1 y1))` | `SIZE (x1-x0) BY (y1-y0)` + `ORIGIN -x0 -y0` | Coordinates converted DBU → microns via `Units.UuUm` / `Units.DbuNm`. LEF origin shifts so the LEF-local frame's `(0,0)` matches `(x0,y0)` in `.rkt`. |
| `(props (description "…"))` | LEF comment line `# DESCRIPTION: …` immediately above the `MACRO` keyword | Comment line so it survives consumers that don't parse `PROPERTY`. |
| `(port (name N) (dir D) (layer L) (flags F…) (shape S))` | `PIN N` with `DIRECTION D ;` + `PORT … LAYER L ; <shape> END` + `USE …` from flags | One `PIN` per port. Direction map below. Flags map below. |
| `(port … (shape (rect x1 y1 x2 y2)))` | `PORT LAYER L ; RECT x1 y1 x2 y2 ; END` | Single rect inside `PORT`. |
| `(port … (shape (poly (p1) (p2) …)))` | `PORT LAYER L ; POLYGON p1 p2 … ; END` | LEF `POLYGON` accepts arbitrary polys. |
| `(label …)` on a non-port net | (ignored by LEF) | Labels are net-derivation hints, not pins. Only `(port …)` elements become `PIN` entries. |
| `(poly …)`, `(path …)`, `(rect …)` on drawn layers | (ignored by LEF) | Interior geometry. Not exposed in the abstract. Drives `OBS` if obstruction policy enabled. |
| `(sref …)` / `(aref …)` | (ignored by LEF) | LEF abstracts don't include child instances. |

### Direction map

| `.rkt` `(dir …)` | LEF `DIRECTION` |
|---|---|
| `input` | `INPUT` |
| `output` | `OUTPUT` |
| `inout` | `INOUT` |
| `unspecified` | omit `DIRECTION` clause |

### Flags → `USE` and `CLASS` map

LEF combines two orthogonal concepts in one `USE` clause. `.rkt`
flags are a flat set. Mapping rules (applied in order; first match
wins, ties resolved by precedence):

| `.rkt` flag(s) | LEF `USE` | LEF `CLASS` |
|---|---|---|
| `power` | `POWER` | (omitted) |
| `ground` | `GROUND` | (omitted) |
| `clock` | `CLOCK` | (omitted) |
| `analog` | `ANALOG` | (omitted) |
| `scan` (with or without `signal`) | `SIGNAL` | `SCAN` |
| `signal` (alone) | `SIGNAL` | (omitted) |
| (no flags) | `SIGNAL` | (omitted) |

Precedence rationale: `power`/`ground`/`clock`/`analog` are mutually
exclusive with each other and with `signal` per LEF semantics, so
the emitter errors if a port carries more than one of those. `scan`
combined with one of those four is also an error (scan implies
signal).

### Obstruction policy

Default (`ObsPolicy.FullSizeMet1Met2`, matching legacy v2 SRAM):
emit `OBS LAYER met1 ; RECT 0 0 W H ; LAYER met2 ; RECT 0 0 W H ; END`
where `W H` is the macro size.

Optional (`ObsPolicy.DerivedFromGeometry`): union of the cell's
drawn rectangles on each obstruction-eligible layer, bbox-flattened
per connected component. Slower; only used when the macro's internal
metal usage is sparse enough that full-size obs would over-block.

Optional (`ObsPolicy.None`): no `OBS` block. Use for cells consumed
by tools that derive obstructions externally.

Obstruction-eligible layers configurable per call; default set is
`met1`, `met2`. (Higher metals usually carry PDN straps that should
NOT be obstructed.)

## API

```fsharp
module Rekolektion.Viz.Core.Rkt.ToLef

open Rekolektion.Viz.Core.Rkt.Types

type ObsPolicy =
    | FullSize of layers: string list
    | DerivedFromGeometry of layers: string list
    | NoObs

type EmitOptions = {
    /// Macro name. If None, use the cell's name verbatim.
    MacroName: string option

    /// Pin-name case policy. `Uppercase` matches v1 Liberty
    /// convention (ADDR/DIN/DOUT/CLK/WE/CS); `Verbatim` preserves
    /// the `(port (name …))` text as-is.
    PinCase: PinCase

    /// Obstruction policy. Default: FullSize ["met1"; "met2"].
    Obstructions: ObsPolicy

    /// Cell `(props (description …))` rendered as a comment above
    /// `MACRO` if Some; suppressed if None.
    EmitDescriptionComment: bool

    /// Manufacturing-grid snap (microns). Default: 0.005 (sky130).
    /// Any input coordinate that doesn't already land on the grid
    /// is an error — `.rkt` integers in DBU should never be
    /// off-grid relative to the unit declaration.
    MfgGridUm: decimal
}

and PinCase = Verbatim | Uppercase

module EmitOptions =
    val defaults : EmitOptions

/// Emit a single cell as a LEF macro.
val emitCell :
    options: EmitOptions ->
    doc: Document ->
    cellName: string ->
    Result<string, EmitError>

/// Emit every top-level cell in a Library / Document set as a single
/// LEF file (concatenated MACRO blocks + one shared header).
val emitLibrary :
    options: EmitOptions ->
    library: Reader.Library ->
    Result<string, EmitError>

type EmitError =
    | MissingBboxProp of cellName: string
    | UnknownLayer of layerRef: LayerRef * cellName: string
    | UnsupportedPortShape of portName: string * cellName: string
    | ConflictingFlags of portName: string * flags: PortFlag list
    | OffGridCoordinate of axis: string * value: decimal * cellName: string
    | NoSuchCell of cellName: string
```

Mirrors the shape of `Writer.write` / `ToGds.toLibrary`: pure
functions, `Result`-typed errors, no I/O in the core module. CLI
adds the file-write wrapper.

## CLI surface

Extend `tools/viz/src/Rekolektion.Viz.Cli/`:

```
viz lef <input.rkt> --output <out.lef> [--cell <name>]
                                       [--uppercase-pins]
                                       [--obs none|fullsize|derived]
                                       [--obs-layers met1,met2,…]
```

Default `--cell` resolves to the document's `(top …)` cell, or the
first cell if `(top …)` is absent. `--cell *` emits every top-level
cell as one LEF library (one MACRO per cell).

Parallel command name to existing `read`, `render`, `mesh`, `app`,
`viz-render` — keeps the CLI verb space coherent.

## MCP tool surface

Add `mcp__rekolektion-viz__rekolektion_viz_emit_lef` to
`tools/viz/src/Rekolektion.Viz.Mcp/`. Mirrors the CLI args. Used by
agents during macro assembly so the LEF is available alongside the
GDS without dropping out to a shell.

## Acceptance criteria

- **A1.** `emitCell` produces a LEF 5.7 file that opens cleanly in
  OpenROAD's `read_lef` with zero warnings on a hand-authored
  3-port test cell (`tests/testdata/simple_macro.rkt`).
- **A2.** Round-trip via OpenROAD: `read_lef`, then dump SIZE and
  every PIN — values match the source `.rkt` exactly (after
  DBU→µm conversion).
- **A3.** Cells without `(props (bbox …))` are rejected with
  `MissingBboxProp`; no silent fallback. Test case must assert this.
- **A4.** Cells with `unknown:N/D` layer references on `(port …)`
  elements are rejected with `UnknownLayer`. Test case must assert
  this.
- **A5.** Pin direction (`input`/`output`/`inout`/`unspecified`) and
  flag combinations (`signal`, `power`, `ground`, `clock`, `analog`,
  `scan`, `signal+scan`) each round-trip to the correct LEF `DIRECTION`
  + `USE`/`CLASS` per the mapping table.
- **A6.** Off-grid coordinate input (synthesized `.rkt` with a port
  shape at, say, `(1.0023, 0.5)` µm) is rejected with
  `OffGridCoordinate`. Test case must assert this.
- **A7.** Same-input determinism: emit twice, compare bytes — equal.
- **A8.** **Cross-validation.** Run the new emitter on a `.rkt`
  representation of the v1 64×8 SRAM macro and diff its output
  against `lef_generator.py`'s output. Document every difference;
  classify each as (a) emitter bug, (b) legacy bug to fix forward,
  (c) intentional schema-driven change. No silent divergence.
- **A9.** First-consumer test: emit LEF for the ReRAM_IRL Track 09
  array's CIM macro `.rkt`, run OpenROAD `read_lef` + a smoke
  `place_macro`, no errors.

## Test fixtures

New under `tools/viz/testdata/lef/`:

| Fixture | Tests |
|---|---|
| `simple_3port.rkt` + `simple_3port.golden.lef` | A1, A2, A7 (golden-file determinism + open-in-OpenROAD) |
| `no_bbox.rkt` | A3 (`MissingBboxProp`) |
| `unknown_layer_port.rkt` | A4 (`UnknownLayer`) |
| `all_directions.rkt` + golden | A5 direction map |
| `all_flag_combos.rkt` + golden | A5 USE/CLASS map |
| `off_grid_port.rkt` | A6 (`OffGridCoordinate`) |
| `cross_val/v1_sram_64x8.rkt` + `cross_val/v1_sram_64x8.legacy.lef` | A8 cross-validation diff |
| `cross_val/reram_track09_array.rkt` | A9 first-consumer smoke |

Golden files regenerated only on intentional emitter change, with a
single-line justification in the commit message (mirrors the
existing `RktToGdsTests` / `GdsWriterTests` discipline).

## Implementation phases

### P0 — module skeleton + bbox + size

- `tools/viz/src/Rekolektion.Viz.Core/Rkt/ToLef.fs`: types
  (`ObsPolicy`, `EmitOptions`, `EmitError`, `PinCase`), `defaults`,
  `emitCell` stub that emits `MACRO/SIZE/ORIGIN/END MACRO` from
  `(props (bbox …))` only. No pins, no obs.
- Layer-name lookup (PDK-qualified → LEF name) reusing the same
  SKY130 table that `ToGds.fs` uses (extract to a shared helper if
  needed).
- `simple_3port.rkt` with bbox-only emit test (A1 subset, A3, A7).

### P1 — pins (direction, layer, shape, USE/CLASS)

- Port → PIN/PORT emission. Direction map. Flag → USE/CLASS map
  including conflict detection.
- `all_directions.rkt`, `all_flag_combos.rkt`, A4, A5.

### P2 — OBS policy

- `FullSize`, `DerivedFromGeometry`, `NoObs`.
- Geometry-derived obs: union polygons per layer via existing
  `Layout.Flatten` / `Layout.Bbox` helpers in
  `Rekolektion.Viz.Core/Layout/`.

### P3 — `emitLibrary`, CLI, MCP

- `emitLibrary` (multi-cell). CLI `viz lef …` command. MCP tool.
- Off-grid detection (A6) with the manufacturing-grid snap helper
  pulled out as a shared utility.

### P4 — cross-validation

- A8 diff against `lef_generator.py` on v1 64×8 SRAM. Resolve every
  difference. Update plan with findings.

### P5 — first consumer (ReRAM_IRL Track 09)

- A9. Wire the emitter into the array assembly flow.
- Coordinate with `khalkulo/conductor/projects/ReRAM_IRL/tracks/09_array_assembly/plan.md`
  on `.rkt` shape requirements.

## Risks

- **Legacy LEF idiosyncrasies.** `lef_generator.py` carries
  OpenLane/OpenROAD-specific compatibility tweaks (the `OBS` policy,
  power-stub geometry, the `uppercase_ports` switch). Cross-validation
  (P4) will surface every one. Plan: classify each, fix-forward
  legacy quirks not justified by toolchain requirements.
- **Pin-name case policy.** v1 Liberty uses uppercase port names;
  the v2 generator already supports both via `uppercase_ports=True`.
  The `PinCase` option preserves this; tests cover both. Risk is
  consumers picking the wrong case and breaking pairing with `.lib`.
  Document the default and the symmetric `.lib` emitter requirement.
- **PDN strap representation.** Power/ground pins in v1 are emitted
  as wide `met4` straps with explicit geometry. `(port …)` with
  `(flags power)` and `(shape (rect …))` covers the same case
  declaratively — but only if the cell's `.rkt` *declares* the strap
  as a port. Generators that paint a met4 polygon but don't add a
  `(port …)` will emit a LEF with no power pin. Address by enforcing
  a "every cell with power must have a `(port (flags power))`
  declaration" check in P4 cross-validation; document the rule in
  `docs/io/rkt.md → Conventions`.
- **`OBS DerivedFromGeometry` cost.** Polygon union on a large CIM
  array could be slow. Default stays `FullSize`; `Derived` is
  opt-in. Add a benchmark fixture.
- **Coordinate-grid drift.** `.rkt` stores integer DBU; LEF emits
  decimal microns. With `dbu_nm=1` and `uu_um=1` the conversion is
  exact (DBU/1000), and 5 nm is exactly 5 DBU — no rounding. Higher
  `dbu_nm` values or non-default `uu_um` would break this; emitter
  errors out if the units don't satisfy `5 DBU ≡ 5 nm exactly`.

## Open questions

- **Should the emitter participate in the `(import …)` graph?**
  i.e., when `cell A` references `cell B` via `(sref …)` and only
  `B` is needed as a LEF MACRO, should the emitter follow imports
  to find `B` in another file? Proposal: yes, use `Reader.loadSingle`
  so the emitter sees the same merged library as the rest of the
  toolchain. Confirm before P0.
- **`(props (description …))` as LEF `PROPERTY` block vs leading
  comment?** Comment is more portable (some LEF parsers reject
  unknown `PROPERTY` keys); comment chosen above. Revisit if a
  consumer needs structured metadata.
- **What about `bbox` in DBU vs LEF-local frame?** The proposal
  shifts LEF origin so `(0,0) LEF ≡ (x0, y0) RKT`. Alternative:
  preserve the original coordinate system, allow negative LEF
  coordinates. Most tools accept negatives but OpenROAD historically
  preferred non-negative. Default to shifted; flag to disable if a
  consumer needs raw coordinates.

## Files affected

- `tools/viz/src/Rekolektion.Viz.Core/Rkt/ToLef.fs` (new) — emitter
  module.
- `tools/viz/src/Rekolektion.Viz.Core/Rkt/` shared helpers — extract
  SKY130 layer-name table from `ToGds.fs` if not already shared;
  extract mfg-grid snap helper.
- `tools/viz/src/Rekolektion.Viz.Cli/Program.fs` — `lef` subcommand.
- `tools/viz/src/Rekolektion.Viz.Mcp/` — new MCP tool
  `rekolektion_viz_emit_lef`.
- `tools/viz/tests/Rekolektion.Viz.Core.Tests/RktToLefTests.fs` (new).
- `tools/viz/testdata/lef/` (new dir) — fixtures listed above.
- `docs/io/rkt.md` — strike the "`Rkt.ToLef` doesn't exist" bullet
  from "Open gaps" once P0 lands; replace with a reference to the
  shipped module.
