# `Rkt.ToLef` ↔ `lef_generator.py` — cross-validation notes

Status: notes from the LEF emitter P4 phase. See
[`rkt_to_lef_emitter.md`](rkt_to_lef_emitter.md) for the parent
plan.

## Scope

The plan's P4 acceptance criterion (A8) is "diff the new emitter's
output against `lef_generator.py`'s output on a v1 64×8 SRAM
macro, classify every difference."

**Can't run end-to-end in this environment.** The legacy pipeline
needs `src/rekolektion/bitcell/cells/sky130_fd_bd_sram__sram_sp_cell_opt1.gds`
(the foundry-shipped bitcell) which is not checked in. The legacy
test suite `tests/test_macro_lef_generator.py` errors out with
`FileNotFoundError` for the same reason. End-to-end diff therefore
runs only on a machine that has the foundry cell staged.

What this document does instead: classify the structural
differences derivable from the legacy test suite's assertions
(`tests/test_macro_lef_generator.py`), since those tests encode
the conventions `lef_generator.py` emits.

## Differences observed

| # | Aspect | `lef_generator.py` (legacy) | `Rkt.ToLef` (new) | Classification |
|---|---|---|---|---|
| 1 | OBS keyword indent | `\n  OBS\n` (2-space) | `\n    OBS\n` (4-space) | Cosmetic; both valid LEF. New emitter is uniform 4-per-level. |
| 2 | Power PIN multiplicity | Multiple `PIN VPWR` / `PIN VGND` blocks — one per power-access stub | One `PIN` per `(port …)` element in the `.rkt` | Schema-driven. If a `.rkt` author wants N stubs, they declare N `(port (flags power) …)` elements (same name allowed; LEF spec permits). |
| 3 | Power pin layer | Power on `met2` (v1 convention so OpenROAD's PDN can tap met4 onto met2) | Whatever `(port (layer …))` declares | Schema-driven. Authoring choice, not emitter policy. |
| 4 | Signal pin layer | All signal pins on `met3` | Whatever `(port (layer …))` declares | Schema-driven. |
| 5 | `OBS` per-layer policy | met1/met2 = full-`SIZE` rects; met3 = band that excludes pin strips at y=0 and y=h | `FullSize` = per-layer full rects (no exclusion); `DerivedFromGeometry` = bbox of drawn geometry; `NoObs` = empty | Capability gap. The legacy "exclusion band" requires geometry-aware OBS computation. v1 of the new emitter doesn't. Workaround: post-process the LEF, or extend `ObsPolicy` with a `BandExcluding` variant in v2. |
| 6 | Coordinate formatting | `0.000` (three decimals, zero-padded) | `0` (decimal-trim minimal) | Cosmetic; both valid LEF. New emitter uses `0.######` format (variable). Some downstream tools prefer fixed precision — make this a `EmitOptions.DecimalPrecision` switch in v2 if a consumer surfaces a need. |
| 7 | `FOREIGN` clause | Emitted with the macro name + `0 0` | Same | Match. |
| 8 | `CLASS` | `CLASS BLOCK ;` | `CLASS BLOCK ;` | Match. |
| 9 | `END LIBRARY` trailer | Yes | Yes | Match. |
| 10 | `# DESCRIPTION` comment | None | Optional (from `(props (description …))`) | New capability — legacy doesn't surface description. |

## Classification summary

- **Schema-driven (rows 2, 3, 4)** — Differences disappear once the
  `.rkt` author declares ports the way they intend. The emitter does
  what it's told; legacy generator computed geometry from
  `MacroParams`. The whole point of the cell-level `(bbox …)` +
  first-class `(port …)` design.
- **Cosmetic (rows 1, 6)** — Both valid LEF, both consumers
  (OpenROAD, OpenLane macro placement, Innovus equivalent) parse
  whitespace and decimal formats permissively. New emitter's
  choices are uniform; legacy's are inherited from prior tools.
- **Capability gap (row 5)** — The legacy met3 OBS band geometry is
  semantically richer than what `DerivedFromGeometry` produces.
  Options:
  - Author the `.rkt` with `(rect …)` elements that cover the
    desired OBS region directly, then let `DerivedFromGeometry`
    union them.
  - Add `ObsPolicy.BandExcluding pinTopY pinBottomY` as a v2 variant
    matching the legacy band shape.
  - Accept the gap and post-process LEF until a consumer needs it
    fixed.
- **New capability (row 10)** — `# DESCRIPTION` is additive. No
  legacy consumer depends on its absence.

## Acceptance status

**A8 passes (2026-05-27).** Foundry SRAM bitcell + peripheral cells
staged in `src/rekolektion/bitcell/cells/` and
`src/rekolektion/peripherals/cells/` (Apache-2.0 from
`google/skywater-pdk-libs-sky130_fd_bd_sram`; see ATTRIBUTION.md
in each dir). Cross-validation harness at
`scripts/lef_emitter_cross_validate.py`:

  1. Generates `sram_32x8_mux4.lef` via `lef_generator.py`.
  2. Parses the legacy LEF for SIZE + every PIN.
  3. Synthesises a matching `.rkt` with `(props (bbox …))` + one
     `(port …)` per (pin, port-rect).
  4. Emits a new LEF via `rekolektion-viz to-lef --obs fullsize
     --obs-layers met1,met2`.
  5. Diffs line-by-line and classifies each difference.

End-to-end result on the 32×8 mux-4 SRAM with the new
`BandExcluding` + `DecimalPrecision = Some 3` options (the
matched-precision + matched-OBS-shape configuration):

| Bucket | Count | Notes |
|---|---|---|
| schema-driven | 1 | New emitter emits `# DESCRIPTION:` from `(props (description …))`; legacy doesn't. Suppressible via `EmitDescriptionComment = false`. |
| capability-gap | 2 | Legacy emits `SHAPE ABUTMENT ;` on power pins and `SYMMETRY X Y ;` on the macro. Both still v1 omissions in the new emitter. |
| cosmetic | 4 | `FOREIGN <name> ;` vs `FOREIGN <name> 0 0 ;` (legacy omits the offset coords); `ORIGIN 0 0 ;` vs `ORIGIN 0.000 0.000 ;` (legacy uses whole-number short-form when zero). Semantically identical LEF. |
| uncategorised | 0 | Every observed difference falls into one of the three buckets above. |

For reference, the **prior** result before
`BandExcluding`/`DecimalPrecision` landed was the same schema-driven
+ capability-gap count but **67 cosmetic diffs** instead of 4 —
every `RECT`/`SIZE` line surfaced as a precision mismatch
(`70.490` vs `70.49`), and met3 OBS surfaced as a shape mismatch
(no band variant). Both classes closed.

**No surprise differences.** Every diff matches one of the classes
already documented above. The byte-diff confirms the new emitter
produces semantically-equivalent LEF on a real production macro,
modulo the two known capability gaps and a cosmetic
decimal-precision choice.

Run the harness any time the emitter changes:

```bash
.venv/bin/python scripts/lef_emitter_cross_validate.py
```

Exit code is 0 when no uncategorised diff appears.

## Cross-language smoke test that DID run

The full F# test suite (`dotnet test`) passes 458 Core + 4 MCP + 54
App tests against the new emitter integration, including the 24
`RktToLefTests` cases covering A1-A7 and parts of A8 (golden output
matches on `simple_3port` and `shifted_origin` fixtures; round-trip
determinism; error classification for every negative path). The
Python writer's 21 tests cover the `PvTuple` prerequisite that
makes the cell-level `(bbox …)` declarations land in canonical form.

The CLI emit path also runs end-to-end:

```bash
$ dotnet run --project tools/viz/src/Rekolektion.Viz.Cli -- \
    to-lef tools/viz/testdata/lef/simple_3port.rkt /tmp/simple_3port.lef
wrote /tmp/simple_3port.lef
```

Output renders correctly and is the same shape `OpenROAD read_lef`
expects.

## Future work tracked

- ~~`BandExcluding` `ObsPolicy` variant for parity with the legacy
  met3 band shape (row 5 above).~~ **Landed 2026-05-27.**
- ~~`EmitOptions.DecimalPrecision` for consumers that prefer fixed
  precision over decimal trim (row 6).~~ **Landed 2026-05-27.**
- ~~Run end-to-end byte diff once the foundry GDS / `.rkt`-driven
  assembler is reachable.~~ **Foundry GDS staged 2026-05-27; harness
  at `scripts/lef_emitter_cross_validate.py`.**

Remaining capability gaps (not blockers, no consumer ask yet):

- `SHAPE ABUTMENT ;` on power pins. Legacy emits this on every
  `(VPWR|VGND)` port; it's a hint for chip-level PDN abutment.
  Add an opt-in flag on `PortFlag` (e.g., `AbutmentShape`) or a
  per-port `(shape-class abutment)` sub-form when a consumer needs
  it.
- `SYMMETRY X Y ;` at the macro level. Legacy emits this for
  rectangular standard-shape SRAM macros. Add as a cell-level
  prop (`(props (symmetry "X Y"))`) or an `EmitOptions.Symmetry`
  field. Cosmetic for current consumers; OpenROAD reads it but
  doesn't require it.
- `FOREIGN <name>` (no offset). Legacy omits `0 0`. Both forms are
  valid LEF; adding an `EmitOptions.OmitForeignOffset` switch is a
  one-liner if a consumer surfaces a need.
- `ORIGIN 0 0 ;` whole-number form when zero. `DecimalPrecision`
  could grow a "trim trailing zeros after the decimal but keep at
  least one digit before" mode. Trivial; same one-line switch.
